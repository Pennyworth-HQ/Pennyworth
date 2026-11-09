using System.Text.Json;
using Pennyworth.Core.Privilege;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>
/// The broker rules, played against the real helper loop over in-memory pipes: one start for any number of requests, a dismissed
/// prompt is final for its action, a session that died comes back only on the next explicit action, it locks on request and when
/// idle, and requests run one at a time. No pkexec, no root, no password.
/// </summary>
public class ElevationSessionTests
{
    private const string Pkexec = Privileged.Pkexec;
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private static ServeRequest Hello() => new(HelperProtocol.Version, 0, HelperProtocol.Hello);

    private static ServeRequest Folders(params string[] paths) => new(HelperProtocol.Version, 0, HelperProtocol.MeasureFolders, Paths: paths);

    private static ElevationSession Session(FakeLauncher launcher, TimeProvider? time = null, TimeSpan? prompt = null, TimeSpan? margin = null) =>
        new(launcher.Launch, time ?? new ManualTime(), prompt ?? TimeSpan.FromSeconds(10), margin ?? TimeSpan.FromSeconds(10));

    private static FakeLauncher Serving(HelperSeams? seams = null, int times = 4) => FakeLauncher.Serving(seams ?? ServeFakes.Seams(), times);

    // ---- one start, many requests ----

    [LinuxFact]
    public async Task The_first_request_starts_the_session_and_every_later_one_reuses_it()
    {
        FakeLauncher launcher = Serving();
        using ElevationSession session = Session(launcher);

        for (int i = 0; i < 5; i++)
        {
            PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(5), Token);
            Assert.Equal(PrivilegedOutcome.Done, answer.Outcome);
            Assert.Equal(HelperExit.Ok, answer.Value!.Exit);
        }

        Assert.Equal(1, launcher.Starts);
        Assert.Equal([$"{Pkexec} {HelperProtocol.InstalledPath} serve"], launcher.Lines);
        Assert.True(session.IsActive);
    }

    [LinuxFact]
    public async Task Requests_made_at_once_are_serialised_and_still_one_start()
    {
        int running = 0;
        int most = 0;
        HelperSeams seams = ServeFakes.Seams(folders: (request, _) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            Thread.Sleep(20);
            Interlocked.Decrement(ref running);
            return new FoldersResponse(HelperProtocol.Version, [new FolderSize(request.Paths[0], 1, 1, 1, true)]);
        });
        FakeLauncher launcher = Serving(seams);
        using ElevationSession session = Session(launcher);

        PrivilegedResult<ServeResponse>[] answers = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => session.RequestAsync(Pkexec, Folders("/f" + i), allowStart: true, TimeSpan.FromSeconds(10), Token)));

        Assert.All(answers, answer => Assert.Equal(PrivilegedOutcome.Done, answer.Outcome));
        Assert.Equal(8, answers.Select(a => a.Value!.Folders!.Folders[0].Path).Distinct().Count());
        Assert.Equal(1, most);
        Assert.Equal(1, launcher.Starts);
    }

    // ---- a dismissed prompt ----

    [LinuxFact]
    public async Task A_dismissed_prompt_is_cancelled_and_is_not_asked_again_by_itself()
    {
        var launcher = new FakeLauncher(() => FakeChannel.Exits(126));
        using ElevationSession session = Session(launcher);

        PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Assert.Equal(PrivilegedOutcome.Cancelled, answer.Outcome);
        Assert.Equal(1, launcher.Starts);
        Assert.False(session.IsActive);
    }

    [LinuxFact]
    public async Task The_next_explicit_action_after_a_dismissed_prompt_asks_again()
    {
        var launcher = new FakeLauncher(() => FakeChannel.Exits(126), () => FakeChannel.Serving(ServeFakes.Seams()));
        using ElevationSession session = Session(launcher);

        PrivilegedResult<ServeResponse> first = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(5), Token);
        PrivilegedResult<ServeResponse> second = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Assert.Equal(PrivilegedOutcome.Cancelled, first.Outcome);
        Assert.Equal(PrivilegedOutcome.Done, second.Outcome);
        Assert.Equal(2, launcher.Starts);
    }

    [LinuxTheory]
    [InlineData(126, "", PrivilegedOutcome.Cancelled, null)]
    [InlineData(127, "Error executing command as another user: Not authorized", PrivilegedOutcome.Cancelled, null)]
    [InlineData(127, "Error getting authority: Error initializing authority: No authentication agent found.", PrivilegedOutcome.NotAvailable, PrivilegedUnavailableReason.NoAuthenticationAgent)]
    [InlineData(2, "pennyworth-helper: unknown subcommand", PrivilegedOutcome.NotAvailable, PrivilegedUnavailableReason.HelperOutdated)]
    [InlineData(1, "pennyworth-helper: boom", PrivilegedOutcome.Failed, null)]
    [InlineData(3, "pennyworth-helper: refused: its parent process does not belong to the user who authenticated", PrivilegedOutcome.Failed, null)]
    [InlineData(137, "", PrivilegedOutcome.Failed, null)]
    public async Task What_pkexec_or_the_helper_says_when_it_ends_without_serving_maps_to_one_outcome_and_one_start(
        int exit, string error, PrivilegedOutcome outcome, PrivilegedUnavailableReason? reason)
    {
        var launcher = new FakeLauncher(() => FakeChannel.Exits(exit, error));
        using ElevationSession session = Session(launcher);

        PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Assert.Equal((outcome, reason), (answer.Outcome, answer.Reason));
        Assert.Equal(1, launcher.Starts);
        Assert.False(session.IsActive);
        if (outcome == PrivilegedOutcome.Failed && error.Length > 0)
        {
            Assert.Equal(error, answer.Detail);
        }
    }

    [LinuxFact]
    public async Task A_helper_that_starts_and_refuses_the_hello_is_a_failure_with_its_reason_and_no_session()
    {
        var launcher = new FakeLauncher(() => FakeChannel.Scripted(request => Reply(request, exit: HelperExit.Refused, error: "refused: no")));
        using ElevationSession session = Session(launcher);

        PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Assert.Equal((PrivilegedOutcome.Failed, "refused: no"), (answer.Outcome, answer.Detail));
        Assert.False(session.IsActive);
    }

    [LinuxFact]
    public async Task A_helper_that_does_not_speak_this_version_is_outdated()
    {
        var launcher = new FakeLauncher(() => FakeChannel.Scripted(request => Reply(request, exit: HelperExit.Usage, error: "unsupported protocol version")));
        using ElevationSession session = Session(launcher);

        PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Assert.Equal(PrivilegedUnavailableReason.HelperOutdated, answer.Reason);
        Assert.False(session.IsActive);
    }

    [LinuxFact]
    public async Task A_prompt_nobody_answers_ends_at_its_timeout_and_the_pkexec_is_stopped()
    {
        var launcher = new FakeLauncher(FakeChannel.Mute);
        using ElevationSession session = Session(launcher, prompt: TimeSpan.FromMilliseconds(200));

        PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Assert.Equal(PrivilegedOutcome.Failed, answer.Outcome);
        Assert.True(launcher.Channels[0].Aborted);
        Assert.Equal(1, launcher.Starts);
    }

    [LinuxFact]
    public async Task Cancelling_the_token_during_the_prompt_stops_pkexec_and_throws()
    {
        var launcher = new FakeLauncher(FakeChannel.Mute);
        using ElevationSession session = Session(launcher);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<PrivilegedResult<ServeResponse>> pending = session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), source.Token);
        await Task.Delay(100, Token);
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(launcher.Channels[0].Aborted);
        Assert.False(session.IsActive);
    }

    [LinuxFact]
    public async Task A_launcher_that_cannot_start_pkexec_is_not_available()
    {
        using var session = new ElevationSession((_, _) => null, new ManualTime(), TimeSpan.FromSeconds(1));

        PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(1), Token);

        Assert.Equal(PrivilegedUnavailableReason.NoPkexec, answer.Reason);
    }

    // ---- a request nobody may prompt for ----

    [LinuxFact]
    public async Task A_request_that_may_not_ask_never_starts_a_session_but_uses_a_running_one()
    {
        FakeLauncher launcher = Serving();
        using ElevationSession session = Session(launcher);

        PrivilegedResult<ServeResponse> cold = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: false, TimeSpan.FromSeconds(5), Token);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);
        PrivilegedResult<ServeResponse> warm = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: false, TimeSpan.FromSeconds(5), Token);

        Assert.Equal(PrivilegedUnavailableReason.NotActive, cold.Reason);
        Assert.Equal(PrivilegedOutcome.Done, warm.Outcome);
        Assert.Equal(1, launcher.Starts);
    }

    // ---- a session that dies, locks or idles ----

    [LinuxFact]
    public async Task A_session_that_crashed_is_restarted_only_by_the_next_request_that_may_ask()
    {
        FakeLauncher launcher = Serving();
        using ElevationSession session = Session(launcher);
        int changes = 0;
        session.Changed += () => Interlocked.Increment(ref changes);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        launcher.Channels[0].Crash();
        Assert.True(SpinWait.SpinUntil(() => !session.IsActive, TimeSpan.FromSeconds(5)));
        PrivilegedResult<ServeResponse> background = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: false, TimeSpan.FromSeconds(5), Token);
        Assert.Equal(PrivilegedUnavailableReason.NotActive, background.Reason);
        Assert.Equal(1, launcher.Starts); // nothing came back by itself

        PrivilegedResult<ServeResponse> explicitly = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Assert.Equal(PrivilegedOutcome.Done, explicitly.Outcome);
        Assert.Equal(2, launcher.Starts);
        Assert.Equal(3, changes); // started, died, started again
    }

    [LinuxFact]
    public async Task A_session_that_dies_during_a_request_fails_that_request_and_is_not_retried()
    {
        FakeChannel? channel = null;
        var launcher = new FakeLauncher(() => channel = FakeChannel.Scripted(request =>
        {
            if (request.Op == HelperProtocol.Hello)
            {
                return Reply(request);
            }

            channel!.Crash();
            return null;
        }));
        using ElevationSession session = Session(launcher);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Assert.Equal(PrivilegedOutcome.Failed, answer.Outcome);
        Assert.False(session.IsActive);
        Assert.Equal(1, launcher.Starts); // the request was not silently run again behind a second prompt
    }

    [LinuxFact]
    public async Task Lock_ends_the_session_and_the_helper_exits_on_its_own_end_of_input()
    {
        FakeLauncher launcher = Serving();
        using ElevationSession session = Session(launcher);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        session.Lock();

        Assert.False(session.IsActive);
        Assert.True(launcher.Channels[0].Aborted);
        Assert.Equal(HelperExit.Ok, await launcher.Channels[0].Exited.WaitAsync(TimeSpan.FromSeconds(5), Token)); // the helper left because its input ended
        PrivilegedResult<ServeResponse> after = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(5), Token);
        Assert.Equal(PrivilegedOutcome.Done, after.Outcome);
        Assert.Equal(2, launcher.Starts);
    }

    [LinuxFact]
    public async Task Lock_in_the_middle_of_a_request_stops_it_without_waiting()
    {
        using var started = new ManualResetEventSlim();
        HelperSeams seams = ServeFakes.Seams(folders: (_, token) =>
        {
            started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            return new FoldersResponse(HelperProtocol.Version, []);
        });
        FakeLauncher launcher = Serving(seams);
        using ElevationSession session = Session(launcher);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Task<PrivilegedResult<ServeResponse>> running = session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(30), Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), Token));
        session.Lock();

        PrivilegedResult<ServeResponse> answer = await running.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(PrivilegedOutcome.Failed, answer.Outcome);
        Assert.False(session.IsActive);
        Assert.Equal(1, launcher.Starts);
    }

    [LinuxFact]
    public async Task An_idle_session_locks_itself_after_fifteen_minutes_and_every_request_restarts_the_wait()
    {
        var time = new ManualTime();
        FakeLauncher launcher = Serving();
        using ElevationSession session = Session(launcher, time);
        int changes = 0;
        session.Changed += () => Interlocked.Increment(ref changes);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        time.Advance(HelperProtocol.IdleTimeout - TimeSpan.FromSeconds(1));
        Assert.True(session.IsActive);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token); // restarts the wait
        time.Advance(HelperProtocol.IdleTimeout - TimeSpan.FromSeconds(1));
        Assert.True(session.IsActive);

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(session.IsActive);
        Assert.True(launcher.Channels[0].Aborted);
        Assert.Equal(1, launcher.Starts);
        Assert.Equal(2, changes); // started, locked
        PrivilegedResult<ServeResponse> next = await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);
        Assert.Equal(PrivilegedOutcome.Done, next.Outcome);
        Assert.Equal(2, launcher.Starts); // the next action prompts again
    }

    [LinuxFact]
    public async Task A_request_in_progress_is_never_idle()
    {
        var time = new ManualTime();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        HelperSeams seams = ServeFakes.Seams(folders: (_, _) =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new FoldersResponse(HelperProtocol.Version, []);
        });
        FakeLauncher launcher = Serving(seams);
        using ElevationSession session = Session(launcher, time);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Task<PrivilegedResult<ServeResponse>> running = session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(30), Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), Token));
        time.Advance(HelperProtocol.IdleTimeout + TimeSpan.FromMinutes(1));
        release.Set();

        Assert.Equal(PrivilegedOutcome.Done, (await running.WaitAsync(TimeSpan.FromSeconds(10), Token)).Outcome);
        Assert.True(session.IsActive);
    }

    [LinuxFact]
    public async Task Disposing_the_session_ends_the_helper()
    {
        FakeLauncher launcher = Serving();
        var session = Session(launcher);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        session.Dispose();

        Assert.True(launcher.Channels[0].Aborted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token));
    }

    // ---- a request that goes wrong inside a running session ----

    [LinuxFact]
    public async Task A_request_that_outlives_its_limit_ends_the_session()
    {
        var launcher = new FakeLauncher(() => FakeChannel.Scripted(request => request.Op == HelperProtocol.Hello ? Reply(request) : null));
        using ElevationSession session = Session(launcher, margin: TimeSpan.Zero);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromMilliseconds(200), Token);

        Assert.Equal(PrivilegedOutcome.Failed, answer.Outcome);
        Assert.False(session.IsActive); // it is not trusted with another request
        Assert.True(launcher.Channels[0].Aborted);
    }

    [LinuxFact]
    public async Task An_answer_that_is_not_the_one_asked_for_ends_the_session()
    {
        var launcher = new FakeLauncher(() => FakeChannel.Scripted(request => request.Op == HelperProtocol.Hello ? Reply(request) : Reply(request with { Id = request.Id + 100 })));
        using ElevationSession session = Session(launcher);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Assert.Equal(PrivilegedOutcome.Failed, answer.Outcome);
        Assert.False(session.IsActive);
    }

    [LinuxFact]
    public async Task Garbage_instead_of_an_answer_ends_the_session()
    {
        var launcher = new FakeLauncher(() => FakeChannel.Scripted(request => request.Op == HelperProtocol.Hello ? Reply(request) : "not json"u8.ToArray()));
        using ElevationSession session = Session(launcher);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);

        PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Assert.Equal(PrivilegedOutcome.Failed, answer.Outcome);
        Assert.False(session.IsActive);
    }

    [LinuxFact]
    public async Task A_helper_error_answer_keeps_the_session_and_is_passed_on()
    {
        FakeLauncher launcher = Serving(ServeFakes.Seams(folders: (_, _) => throw new HelperException(HelperExit.Refused, "no")));
        using ElevationSession session = Session(launcher);

        PrivilegedResult<ServeResponse> answer = await session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(5), Token);

        Assert.Equal(PrivilegedOutcome.Done, answer.Outcome);
        Assert.Equal((HelperExit.Refused, "no"), (answer.Value!.Exit, answer.Value.Error));
        Assert.True(session.IsActive);
    }

    [LinuxFact]
    public async Task Cancelling_a_running_request_ends_the_session_because_the_helper_cannot_be_told_to_stop_one()
    {
        using var started = new ManualResetEventSlim();
        HelperSeams seams = ServeFakes.Seams(folders: (_, token) =>
        {
            started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            return new FoldersResponse(HelperProtocol.Version, []);
        });
        FakeLauncher launcher = Serving(seams);
        using ElevationSession session = Session(launcher);
        await session.RequestAsync(Pkexec, Hello(), allowStart: true, TimeSpan.FromSeconds(5), Token);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<PrivilegedResult<ServeResponse>> running = session.RequestAsync(Pkexec, Folders("/root"), allowStart: true, TimeSpan.FromSeconds(30), source.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), Token));
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(session.IsActive);
        Assert.True(launcher.Channels[0].Aborted);
    }

    private static byte[] Reply(ServeRequest request, int exit = HelperExit.Ok, string? error = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new ServeResponse(HelperProtocol.Version, request.Id, exit, error), HelperJson.Default.ServeResponse);
}
