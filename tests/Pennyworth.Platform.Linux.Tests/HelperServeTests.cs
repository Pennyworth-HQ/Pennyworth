using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>
/// <c>pennyworth-helper serve</c> run in-process over real pipes with fabricated machine access: framing, limits, versioning,
/// refusals that leave the session alive, the ways a session ends, serialisation of requests, the peer check, and that a
/// session cleans and measures with the very code and refusals of the one-shot subcommands. No root and no pkexec.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class HelperServeTests : IDisposable
{
    private readonly List<IDisposable> _open = [];
    private readonly TempTree _tree = new();

    public void Dispose()
    {
        foreach (IDisposable item in _open)
        {
            item.Dispose();
        }

        _tree.Dispose();
    }

    private ServeHarness Start(HelperSeams? seams = null)
    {
        var harness = new ServeHarness(seams ?? ServeFakes.Seams());
        _open.Add(harness);
        return harness;
    }

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    // ---- the operations ----

    [LinuxFact]
    public void Hello_proves_the_version_and_nothing_more()
    {
        ServeHarness session = Start();

        ServeResponse response = session.Ask(HelperProtocol.Hello, id: 7);

        Assert.Equal(new ServeResponse(HelperProtocol.Version, 7, HelperExit.Ok), response);
    }

    [LinuxFact]
    public void Each_operation_answers_what_its_one_shot_subcommand_answers_and_the_id_comes_back()
    {
        ServeHarness session = Start();

        ServeResponse firmware = session.Ask(HelperProtocol.ReadFirmwareTables, id: 11);
        ServeResponse space = session.Ask(HelperProtocol.MeasureSystemSpace, "/", id: 12);
        ServeResponse folders = session.Ask(HelperProtocol.MeasureFolders, paths: ["/root", "/lost+found"], id: 13);
        ServeResponse clean = session.Ask(HelperProtocol.CleanSystem, "system-crash-reports", files: [], id: 14);

        Assert.Equal((11L, HelperExit.Ok, "AAAA"), (firmware.Id, firmware.Exit, firmware.Firmware!.Table));
        Assert.Equal((12L, "ext4", 11L, 22L), (space.Id, space.Space!.FileSystem, space.Space.SnapshotBytes, space.Space.MetadataBytes));
        Assert.Equal(["/root", "/lost+found"], folders.Folders!.Folders.Select(f => f.Path));
        Assert.Equal(13, folders.Id);
        Assert.Equal((14L, 5L, 1L), (clean.Id, clean.Clean!.FreedBytes, clean.Clean.Deleted));
        Assert.All([firmware, space, folders, clean], response => Assert.Equal(HelperExit.Ok, response.Exit));
    }

    [LinuxFact]
    public void A_mount_point_must_be_one_the_kernel_lists_exactly_as_in_a_one_shot_run()
    {
        ServeHarness session = Start();

        foreach (string bad in new[] { "/home", "relative", "/../", "/a/../b", "/trail/", "" })
        {
            ServeResponse response = session.Ask(HelperProtocol.MeasureSystemSpace, bad);
            Assert.Equal(HelperExit.Refused, response.Exit);
            Assert.Null(response.Space);
        }

        Assert.Equal(HelperExit.Usage, session.Ask(HelperProtocol.MeasureSystemSpace).Exit); // no argument at all
        Assert.Equal(HelperExit.Ok, session.Ask(HelperProtocol.Hello).Exit); // still alive
    }

    [LinuxFact]
    public void Too_many_folders_are_refused_by_size_and_the_session_goes_on()
    {
        ServeHarness session = Start();
        string[] paths = [.. Enumerable.Range(0, HelperProtocol.MaxFolders + 1).Select(i => "/p" + i)];

        ServeResponse response = session.Ask(HelperProtocol.MeasureFolders, paths: paths);

        Assert.Equal(HelperExit.TooLarge, response.Exit);
        Assert.Equal(HelperExit.Ok, session.Ask(HelperProtocol.Hello).Exit);
    }

    // ---- refusals that leave the session alive ----

    [LinuxFact]
    public void Unknown_and_malformed_requests_are_answered_with_an_error_and_the_session_survives()
    {
        ServeHarness session = Start();

        ServeResponse unknown = session.Ask("read-file", "/etc/shadow", id: 21);
        Assert.Equal((21L, HelperExit.Usage, "unknown operation"), (unknown.Id, unknown.Exit, unknown.Error));

        session.SendFrame(Json("this is not json"));
        ServeResponse garbage = session.Receive();
        Assert.Equal((0L, HelperExit.Usage), (garbage.Id, garbage.Exit));

        session.SendFrame(Json("null"));
        Assert.Equal(HelperExit.Usage, session.Receive().Exit);

        session.SendFrame(Json("""{"version":1,"id":5}"""));
        Assert.Equal(HelperExit.Usage, session.Receive().Exit); // no op

        session.SendFrame([]);
        Assert.Equal(HelperExit.Usage, session.Receive().Exit); // an empty frame

        session.SendFrame(Json("""{"version":1,"id":6,"op":"measure-folders"}"""));
        Assert.Equal(HelperExit.Usage, session.Receive().Exit); // no paths

        session.SendFrame(Json("""{"version":1,"id":8,"op":"clean-system","argument":"system-crash-reports"}"""));
        Assert.Equal(HelperExit.Usage, session.Receive().Exit); // no files

        Assert.False(session.Exit.IsCompleted);
        Assert.Equal(HelperExit.Ok, session.Ask(HelperProtocol.Hello, id: 22).Exit);
    }

    [LinuxFact]
    public void The_one_shot_subcommand_names_are_the_only_operations_and_a_name_has_to_match_exactly()
    {
        ServeHarness session = Start();

        foreach (string op in new[] { "serve", "READ-FIRMWARE-TABLES", "read-firmware-tables ", "", "exec", "shell", "cat", "dmidecode", "btrfs" })
        {
            Assert.Equal(HelperExit.Usage, session.Ask(op).Exit);
        }
    }

    [LinuxFact]
    public void Another_protocol_version_is_refused_with_the_id_echoed_and_the_session_survives()
    {
        ServeHarness session = Start();

        session.Send(new ServeRequest(HelperProtocol.Version + 1, 31, HelperProtocol.Hello));
        ServeResponse response = session.Receive();

        Assert.Equal((31L, HelperExit.Usage, HelperProtocol.Version), (response.Id, response.Exit, response.Version));
        Assert.Equal(HelperExit.Ok, session.Ask(HelperProtocol.Hello).Exit);
    }

    [LinuxFact]
    public void An_action_that_throws_is_a_plain_failure_without_the_cause()
    {
        HelperSeams seams = ServeFakes.Seams(firmware: _ => throw new InvalidOperationException("/secret/path leaked"));
        ServeHarness session = Start(seams);

        ServeResponse response = session.Ask(HelperProtocol.ReadFirmwareTables);

        Assert.Equal((HelperExit.Failed, "unexpected failure"), (response.Exit, response.Error));
        Assert.Equal(HelperExit.Ok, session.Ask(HelperProtocol.Hello).Exit);
    }

    [LinuxFact]
    public void An_action_that_refuses_keeps_its_exit_code_and_a_code_outside_the_table_is_a_plain_failure()
    {
        HelperSeams seams = ServeFakes.Seams(
            firmware: _ => throw new HelperException(HelperExit.Missing, "no table"),
            space: (_, _) => throw new HelperException(127, "pretends to be pkexec"));
        ServeHarness session = Start(seams);

        Assert.Equal((HelperExit.Missing, "no table"), (session.Ask(HelperProtocol.ReadFirmwareTables).Exit, session.Ask(HelperProtocol.ReadFirmwareTables).Error));
        Assert.Equal(HelperExit.Failed, session.Ask(HelperProtocol.MeasureSystemSpace, "/").Exit);
    }

    [LinuxFact]
    public void An_answer_larger_than_the_limit_is_replaced_by_a_too_large_error()
    {
        HelperSeams seams = ServeFakes.Seams(firmware: _ => new FirmwareTablesResponse(HelperProtocol.Version, new string('A', HelperProtocol.MaxResponseBytes)));
        ServeHarness session = Start(seams);

        Assert.Equal(HelperExit.TooLarge, session.Ask(HelperProtocol.ReadFirmwareTables).Exit);
        Assert.Equal(HelperExit.Ok, session.Ask(HelperProtocol.Hello).Exit);
    }

    // ---- framing ----

    [LinuxFact]
    public void A_frame_announced_larger_than_the_limit_ends_the_session_without_reading_it()
    {
        ServeHarness session = Start();
        byte[] header = [0, 0, 0, 0];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)HelperProtocol.MaxRequestBytes + 1);

        session.SendRaw(header);

        Assert.Equal(HelperExit.TooLarge, session.Receive().Exit);
        Assert.Equal(HelperExit.TooLarge, session.WaitForExit());
        Assert.StartsWith("pennyworth-helper: ", session.Error);
    }

    [LinuxFact]
    public void A_frame_cut_short_ends_the_session()
    {
        ServeHarness session = Start();

        session.SendRaw([0, 0, 0, 10, 1, 2, 3]);
        session.CloseInput();

        Assert.Equal(HelperExit.Usage, session.WaitForExit());
    }

    [LinuxFact]
    public void A_header_cut_short_ends_the_session()
    {
        ServeHarness session = Start();

        session.SendRaw([0, 0]);
        session.CloseInput();

        Assert.Equal(HelperExit.Usage, session.WaitForExit());
    }

    [LinuxFact]
    public void A_frame_of_exactly_the_limit_is_read_in_full()
    {
        ServeHarness session = Start();
        string padding = new(' ', 64 * 1024);
        byte[] frame = Json("""{"version":1,"id":3,"op":"hello"}""" + padding);

        session.SendFrame(frame);

        Assert.Equal(3, session.Receive().Id);
    }

    [LinuxFact]
    public void Frames_round_trip_and_the_length_is_big_endian()
    {
        using var stream = new MemoryStream();
        ServeFrames.Write(stream, [1, 2, 3]);

        Assert.Equal([0, 0, 0, 3, 1, 2, 3], stream.ToArray());
        stream.Position = 0;
        Assert.Equal(FrameStatus.Frame, ServeFrames.Read(stream, 10, out byte[] body));
        Assert.Equal([1, 2, 3], body);
        Assert.Equal(FrameStatus.EndOfStream, ServeFrames.Read(stream, 10, out _));
    }

    [LinuxFact]
    public async Task The_async_reader_and_writer_agree_with_the_sync_ones()
    {
        using var stream = new MemoryStream();
        await ServeFrames.WriteAsync(stream, new byte[] { 9, 8 }, TestContext.Current.CancellationToken);
        stream.Position = 0;

        (FrameStatus status, byte[] body) = await ServeFrames.ReadAsync(stream, 2, TestContext.Current.CancellationToken);
        (FrameStatus end, _) = await ServeFrames.ReadAsync(stream, 2, TestContext.Current.CancellationToken);

        Assert.Equal(FrameStatus.Frame, status);
        Assert.Equal(new byte[] { 9, 8 }, body);
        Assert.Equal(FrameStatus.EndOfStream, end);
        stream.SetLength(0);
        await ServeFrames.WriteAsync(stream, new byte[3], TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal(FrameStatus.TooLarge, (await ServeFrames.ReadAsync(stream, 2, TestContext.Current.CancellationToken)).Status);
    }

    // ---- how a session ends ----

    [LinuxFact]
    public void Closing_standard_input_ends_the_session_with_success()
    {
        ServeHarness session = Start();
        session.Ask(HelperProtocol.Hello);

        session.CloseInput();

        Assert.Equal(HelperExit.Ok, session.WaitForExit());
        Assert.Equal(FrameStatus.EndOfStream, session.ReceiveEnd());
    }

    [LinuxFact]
    public void Closing_standard_input_in_the_middle_of_a_request_cancels_it_and_sends_no_answer()
    {
        using var started = new ManualResetEventSlim();
        bool cancelled = false;
        HelperSeams seams = ServeFakes.Seams(folders: (_, token) =>
        {
            started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            cancelled = token.IsCancellationRequested;
            return new FoldersResponse(HelperProtocol.Version, []);
        });
        ServeHarness session = Start(seams);

        session.Send(new ServeRequest(HelperProtocol.Version, 1, HelperProtocol.MeasureFolders, Paths: ["/x"]));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        session.CloseInput();

        Assert.Equal(HelperExit.Ok, session.WaitForExit());
        Assert.True(cancelled);
    }

    [LinuxFact]
    public void A_session_that_sits_idle_ends_by_itself_and_every_request_restarts_the_wait()
    {
        var time = new ManualTime();
        ServeHarness session = Start(ServeFakes.Seams(time));
        session.Ask(HelperProtocol.Hello);

        time.Advance(HelperProtocol.IdleTimeout - TimeSpan.FromSeconds(1));
        Assert.False(session.Exit.IsCompleted);
        session.Ask(HelperProtocol.Hello); // the wait starts over
        time.Advance(HelperProtocol.IdleTimeout - TimeSpan.FromSeconds(1));
        Assert.False(session.Exit.IsCompleted);
        session.Ask(HelperProtocol.Hello);

        time.Advance(HelperProtocol.IdleTimeout);

        Assert.Equal(HelperExit.Ok, session.WaitForExit());
        Assert.Contains("no request for 15 minutes", session.Error);
    }

    [LinuxFact]
    public void The_idle_time_is_fifteen_minutes_and_a_request_in_progress_is_never_idle()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), HelperProtocol.IdleTimeout);

        var time = new ManualTime();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        ServeHarness session = Start(ServeFakes.Seams(time, folders: (r, _) =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new FoldersResponse(HelperProtocol.Version, []);
        }));

        session.Send(new ServeRequest(HelperProtocol.Version, 1, HelperProtocol.MeasureFolders, Paths: ["/x"]));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        time.Advance(HelperProtocol.IdleTimeout + TimeSpan.FromMinutes(1)); // longer than idle, shorter than MaxRuntime

        Assert.False(session.Exit.IsCompleted);
        release.Set();
        Assert.Equal(HelperExit.Ok, session.Receive().Exit);
    }

    [LinuxFact]
    public void A_request_that_runs_past_the_limit_is_cancelled_with_a_timeout_error_and_the_session_goes_on()
    {
        var time = new ManualTime();
        using var started = new ManualResetEventSlim();
        HelperSeams seams = ServeFakes.Seams(time, folders: (_, token) =>
        {
            started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
            return new FoldersResponse(HelperProtocol.Version, []);
        });
        ServeHarness session = Start(seams);

        session.Send(new ServeRequest(HelperProtocol.Version, 4, HelperProtocol.MeasureFolders, Paths: ["/x"]));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        time.Advance(HelperProtocol.MaxRuntime + TimeSpan.FromSeconds(1));

        ServeResponse response = session.Receive();
        Assert.Equal((4L, HelperExit.TimedOut), (response.Id, response.Exit));
        Assert.Equal(HelperExit.Ok, session.Ask(HelperProtocol.Hello).Exit);
    }

    [LinuxFact]
    public void A_request_that_ignores_its_cancellation_ends_the_process_after_the_grace_time()
    {
        var time = new ManualTime();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var terminated = new TaskCompletionSource<int>();
        HelperSeams seams = ServeFakes.Seams(
            time,
            folders: (_, _) =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10)); // deaf to the token, like a hung file system
                return new FoldersResponse(HelperProtocol.Version, []);
            },
            terminate: code => terminated.TrySetResult(code));
        ServeHarness session = Start(seams);

        session.Send(new ServeRequest(HelperProtocol.Version, 1, HelperProtocol.MeasureFolders, Paths: ["/x"]));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        session.CloseInput();
        SpinWait.SpinUntil(() => time.Armed >= 2, TimeSpan.FromSeconds(10)); // the grace timer is armed once the helper noticed
        time.Advance(HelperServe.HardStopGrace + TimeSpan.FromSeconds(1));

        Assert.Equal(HelperExit.TimedOut, terminated.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
        release.Set();
    }

    // ---- one request at a time ----

    [LinuxFact]
    public void Requests_sent_together_run_one_after_another_in_order()
    {
        int running = 0;
        int most = 0;
        var order = new List<string>();
        HelperSeams seams = ServeFakes.Seams(folders: (request, _) =>
        {
            int now = Interlocked.Increment(ref running);
            most = Math.Max(most, now);
            Thread.Sleep(30);
            lock (order)
            {
                order.Add(request.Paths[0]);
            }

            Interlocked.Decrement(ref running);
            return new FoldersResponse(HelperProtocol.Version, [new FolderSize(request.Paths[0], 1, 1, 1, true)]);
        });
        ServeHarness session = Start(seams);

        for (long i = 1; i <= 6; i++)
        {
            session.Send(new ServeRequest(HelperProtocol.Version, i, HelperProtocol.MeasureFolders, Paths: ["/f" + i]));
        }

        ServeResponse[] answers = [.. Enumerable.Range(0, 6).Select(_ => session.Receive())];

        Assert.Equal([1L, 2, 3, 4, 5, 6], answers.Select(a => a.Id));
        Assert.Equal(["/f1", "/f2", "/f3", "/f4", "/f5", "/f6"], order);
        Assert.Equal(1, most);
    }

    // ---- who may start it, and who it serves ----

    [LinuxFact]
    public void Run_as_root_without_pkexec_it_refuses_and_writes_nothing_to_stdout()
    {
        ServeHarness session = Start(ServeFakes.Seams(caller: ServeFakes.Caller(0, pkexecUid: null)));

        Assert.Equal(HelperExit.Refused, session.WaitForExit());
        Assert.Contains("not started through pkexec", session.Error);
        Assert.Equal(FrameStatus.EndOfStream, session.ReceiveEnd());
    }

    [LinuxFact]
    public void Run_as_root_for_root_or_a_system_account_it_refuses()
    {
        foreach (string uid in new[] { "0", "999", "abc", "", "-1" })
        {
            ServeHarness session = Start(ServeFakes.Seams(caller: ServeFakes.Caller(0, uid)));
            Assert.Equal(HelperExit.Refused, session.WaitForExit());
        }
    }

    [LinuxFact]
    public void As_root_standard_input_and_output_must_be_pipes()
    {
        var peer = new PeerSources(() => 4242, _ => 1000, () => false);
        ServeHarness session = Start(ServeFakes.Seams(peer: peer));

        Assert.Equal(HelperExit.Refused, session.WaitForExit());
        Assert.Contains("not pipes", session.Error);
    }

    [LinuxFact]
    public void As_root_the_parent_must_be_a_real_process_of_the_user_who_authenticated()
    {
        foreach ((int parent, uint? owner) in new (int, uint?)[] { (1, 1000), (0, 1000), (4242, 1001), (4242, 0), (4242, null) })
        {
            var peer = new PeerSources(() => parent, _ => owner, () => true);
            ServeHarness session = Start(ServeFakes.Seams(peer: peer));

            Assert.Equal(HelperExit.Refused, session.WaitForExit());
            Assert.Equal(FrameStatus.EndOfStream, session.ReceiveEnd());
        }
    }

    [LinuxFact]
    public void Without_root_nothing_about_the_peer_is_checked_because_the_helper_has_the_callers_own_rights()
    {
        var peer = new PeerSources(() => 1, _ => null, () => false);
        ServeHarness session = Start(ServeFakes.Seams(caller: ServeFakes.Caller(1000, null), peer: peer));

        Assert.Equal(HelperExit.Ok, session.Ask(HelperProtocol.Hello).Exit);
    }

    [LinuxFact]
    public void Losing_the_parent_ends_the_session_and_cancels_the_request_in_progress()
    {
        int parent = 4242;
        var time = new ManualTime();
        using var started = new ManualResetEventSlim();
        bool cancelled = false;
        var peer = new PeerSources(() => Volatile.Read(ref parent), _ => 1000, () => true);
        HelperSeams seams = ServeFakes.Seams(time, peer: peer, folders: (_, token) =>
        {
            started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            cancelled = token.IsCancellationRequested;
            return new FoldersResponse(HelperProtocol.Version, []);
        });
        ServeHarness session = Start(seams);
        Assert.Equal(HelperExit.Ok, session.Ask(HelperProtocol.Hello).Exit);
        time.Advance(HelperProtocol.ParentCheckInterval * 3);
        Assert.False(session.Exit.IsCompleted); // the same parent: nothing happens

        session.Send(new ServeRequest(HelperProtocol.Version, 1, HelperProtocol.MeasureFolders, Paths: ["/x"]));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Volatile.Write(ref parent, 1); // the app died and init adopted the helper
        time.Advance(HelperProtocol.ParentCheckInterval);

        Assert.Equal(HelperExit.Ok, session.WaitForExit());
        Assert.True(cancelled);
    }

    [LinuxFact]
    public void The_real_peer_sources_see_this_process_as_the_parent_of_a_child_and_a_pipe_as_a_pipe()
    {
        Assert.Equal(CallerCheckNative.GetEffectiveUserId(), PeerCheckNative.OwnerOfProcess(Environment.ProcessId));
        Assert.Null(PeerCheckNative.OwnerOfProcess(int.MaxValue));
        Assert.Equal(1000u, PeerCheckNative.ParseRealUid("Name:\tx\nUid:\t1000\t1000\t1000\t1000\n"));
        Assert.Null(PeerCheckNative.ParseRealUid("Name:\tx\n"));
        Assert.Null(PeerCheckNative.ParseRealUid("Uid:\tabc\n"));
        Assert.True(PeerCheckNative.GetParentPid() > 0);
    }

    // ---- the same safety as the one-shot cleanup, through a session ----

    private string Root => _tree.PathOf("root");

    private HelperSeams CleaningSeams() => ServeFakes.Seams(clean: (_, request, token) =>
        SystemCleanup.Run([Root], request, new CleanupPolicy(_ => TrustVerdict.Trusted, _tree.Owner, null, (_, _) => false), token));

    private CleanFile Listed(string relative, long size = 10) => new(Root, relative, size, TempTree.Ticks(Path.Combine(Root, relative)));

    [LinuxFact]
    public void A_session_deletes_the_previewed_files_and_only_them()
    {
        string a = _tree.Write("root/a.rpm", 100);
        string unlisted = _tree.Write("root/b.rpm", 10);
        ServeHarness session = Start(CleaningSeams());

        ServeResponse response = session.Ask(HelperProtocol.CleanSystem, "system-crash-reports", files: [Listed("a.rpm", 100)]);

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 100, 1, 0), response.Clean);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(unlisted));
    }

    [LinuxFact]
    public void A_session_never_follows_a_symlink_out_of_the_folder()
    {
        string outside = _tree.Write("outside/x.rpm", 20);
        Directory.CreateDirectory(Root);
        Directory.CreateSymbolicLink(Path.Combine(Root, "dirlink"), _tree.PathOf("outside"));
        File.CreateSymbolicLink(Path.Combine(Root, "filelink.rpm"), outside);
        ServeHarness session = Start(CleaningSeams());

        ServeResponse through = session.Ask(HelperProtocol.CleanSystem, "system-crash-reports", files: [new CleanFile(Root, "dirlink/x.rpm", 20, TempTree.Ticks(outside))]);
        ServeResponse direct = session.Ask(HelperProtocol.CleanSystem, "system-crash-reports", files: [Listed("filelink.rpm", 3)]);

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 0, 0, 1), through.Clean);
        Assert.Equal(1, direct.Clean!.Deleted); // the link itself goes
        Assert.True(File.Exists(outside));
    }

    [LinuxFact]
    public void A_session_refuses_dot_dot_and_absolute_paths_and_deletes_nothing_of_that_request()
    {
        string good = _tree.Write("root/good.rpm");
        string target = _tree.Write("outside.rpm");
        ServeHarness session = Start(CleaningSeams());

        foreach (string path in new[] { "../outside.rpm", "/etc/passwd", "a/../../outside.rpm", "a//b", "" })
        {
            ServeResponse response = session.Ask(HelperProtocol.CleanSystem, "system-crash-reports", files: [Listed("good.rpm"), new CleanFile(Root, path, 1, 1)]);
            Assert.Equal(HelperExit.Refused, response.Exit);
        }

        Assert.True(File.Exists(good));
        Assert.True(File.Exists(target));
    }

    [LinuxFact]
    public void A_session_keeps_a_file_changed_since_the_preview()
    {
        string path = _tree.Write("root/a.rpm");
        CleanFile listed = Listed("a.rpm");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        ServeHarness session = Start(CleaningSeams());

        ServeResponse response = session.Ask(HelperProtocol.CleanSystem, "system-crash-reports", files: [listed]);

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 0, 0, 1), response.Clean);
        Assert.True(File.Exists(path));
    }

    [LinuxFact]
    public void A_session_does_not_cross_into_another_device()
    {
        string across = _tree.Write("root/mnt/inner.rpm");
        string beside = _tree.Write("root/ok/inner.rpm");
        ulong inode = DirectoryInode(Path.Combine(Root, "mnt"));
        HelperSeams seams = ServeFakes.Seams(clean: (_, request, token) =>
            SystemCleanup.Run([Root], request, new CleanupPolicy(_ => TrustVerdict.Trusted, _tree.Owner, (device, i) => i == inode ? device ^ 1 : device, (_, _) => false), token));
        ServeHarness session = Start(seams);

        ServeResponse response = session.Ask(HelperProtocol.CleanSystem, "system-crash-reports", files: [Listed("mnt/inner.rpm"), Listed("ok/inner.rpm")]);

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 10, 1, 1), response.Clean);
        Assert.True(File.Exists(across));
        Assert.False(File.Exists(beside));
    }

    private static ulong DirectoryInode(string path)
    {
        Assert.Equal(StatResult.Ok, Statx.StatPath(path, followLinks: false, out FileStat stat));
        return stat.Inode;
    }

    [LinuxFact]
    public void A_session_refuses_to_clean_as_a_caller_that_is_not_root_and_for_a_rule_it_does_not_know()
    {
        ServeHarness nonRoot = Start(ServeFakes.Seams(caller: ServeFakes.Caller(1000, null)));
        Assert.Equal(HelperExit.Refused, nonRoot.Ask(HelperProtocol.CleanSystem, "system-crash-reports", files: []).Exit);

        ServeHarness root = Start();
        Assert.Equal(HelperExit.Refused, root.Ask(HelperProtocol.CleanSystem, "../../etc", files: []).Exit);
        Assert.Equal(HelperExit.Refused, root.Ask(HelperProtocol.CleanSystem, "everything", files: []).Exit);
        Assert.Equal(HelperExit.Usage, root.Ask(HelperProtocol.CleanSystem, files: []).Exit); // no rule
    }

    [LinuxFact]
    public void A_session_measures_folders_through_the_same_code_as_the_one_shot_run()
    {
        _tree.Write("root/a.bin", 5000);
        _tree.Write("root/sub/b.bin", 7000);
        HelperSeams seams = ServeFakes.Seams(folders: (request, token) => FolderMeasure.Run(request, token));
        ServeHarness session = Start(seams);

        ServeResponse response = session.Ask(HelperProtocol.MeasureFolders, paths: [Root, "relative", "/nonexistent-dir-xyz"]);

        FolderSize measured = Assert.Single(response.Folders!.Folders);
        Assert.Equal(Root, measured.Path);
        Assert.Equal(12000, measured.LogicalSize);
        Assert.True(measured.Complete);
    }
}
