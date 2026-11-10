using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>One command line the app tried to run, as the fake runner saw it.</summary>
internal sealed record RunCall(string File, string[] Arguments, byte[]? Input, int MaxOutput, TimeSpan Timeout, CancellationToken Token)
{
    public string Line => string.Join(' ', [File, .. Arguments]);
}

/// <summary>
/// Plays pkexec: answers in order and records every command line, its standard input and its limits. A run nobody
/// prepared an answer for throws, which is how a test proves that nothing was run (or asked a second time).
/// </summary>
internal sealed class FakeRunner(params ElevatedRun[] answers)
{
    private readonly Queue<ElevatedRun> _answers = new(answers);

    public List<RunCall> Calls { get; } = [];

    public void Enqueue(ElevatedRun answer) => _answers.Enqueue(answer);

    public IEnumerable<string> Lines => Calls.Select(call => call.Line);

    public Task<ElevatedRun> Run(string file, IReadOnlyList<string> arguments, byte[]? input, int maxOutput, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add(new RunCall(file, [.. arguments], input, maxOutput, timeout, cancellationToken));
        return _answers.Count > 0
            ? Task.FromResult(_answers.Dequeue())
            : throw new InvalidOperationException($"Unexpected run: {Calls[^1].Line}");
    }
}

/// <summary>Answers and file systems the privileged tests share.</summary>
internal static class Privileged
{
    public const string Pkexec = "/usr/bin/pkexec";
    public const string Helper = HelperProtocol.InstalledPath;

    public static ElevatedRun Exit(int code, byte[]? output = null, string error = "") => new(ElevatedStatus.Exited, code, output ?? [], error);

    public static ElevatedRun Text(string text) => Exit(0, Encoding.UTF8.GetBytes(text));

    public static ElevatedRun Json<T>(T value, JsonTypeInfo<T> type) => Exit(0, JsonSerializer.SerializeToUtf8Bytes(value, type));

    public static T Parse<T>(byte[]? bytes, JsonTypeInfo<T> type) => JsonSerializer.Deserialize(bytes!, type)!;

    /// <summary>A file system in which the helper and every folder above it belong to root and nobody else may write there.</summary>
    public static EntryInfo TrustedLstat(string path) => path == Helper
        ? new EntryInfo(EntryKind.File, 0, 0b111_101_101)
        : IsAncestor(path) ? new EntryInfo(EntryKind.Directory, 0, 0b111_101_101) : new EntryInfo(EntryKind.Missing, 0, 0);

    /// <summary>The same, with nothing installed at the helper's path.</summary>
    public static EntryInfo MissingLstat(string path) => path == Helper ? new EntryInfo(EntryKind.Missing, 0, 0) : TrustedLstat(path);

    /// <summary>The same, but the helper's own folder is writable by its group.</summary>
    public static EntryInfo GroupWritableLstat(string path) => path == "/usr/libexec/pennyworth" ? new EntryInfo(EntryKind.Directory, 0, 0b111_111_101) : TrustedLstat(path);

    /// <summary>The same, but the helper is owned by an ordinary user.</summary>
    public static EntryInfo UserOwnedLstat(string path) => path == Helper ? new EntryInfo(EntryKind.File, 1000, 0b111_101_101) : TrustedLstat(path);

    private static bool IsAncestor(string path) => path is "/" or "/usr" or "/usr/libexec" or "/usr/libexec/pennyworth";

    public const string AppFolder = "/opt/pennyworth/lib/pennyworth";

    public static LinuxPrivilegedOperations Ops(
        FakeRunner runner,
        Func<string, EntryInfo> lstat,
        string[]? installed = null,
        string mountInfo = "",
        VfsCounters? counters = null,
        TimeSpan? timeout = null,
        FakeLauncher? launcher = null,
        TimeProvider? time = null,
        Func<string?>? script = null,
        SessionLauncher? launch = null) =>
        Ops(runner.Run, lstat, installed, mountInfo, counters, timeout, launcher, time, script, launch);

    public static LinuxPrivilegedOperations Ops(
        ElevatedRunnerFunc run,
        Func<string, EntryInfo> lstat,
        string[]? installed = null,
        string mountInfo = "",
        VfsCounters? counters = null,
        TimeSpan? timeout = null,
        FakeLauncher? launcher = null,
        TimeProvider? time = null,
        Func<string?>? script = null,
        SessionLauncher? launch = null)
    {
        string[] present = installed ?? [Pkexec, "/usr/bin/cat", "/usr/sbin/dmidecode", "/usr/bin/btrfs", "/usr/bin/bash"];
        return new LinuxPrivilegedOperations(
            run,
            launch ?? (launcher ?? new FakeLauncher()).Launch,
            present.Contains,
            lstat,
            timeout ?? TimeSpan.FromSeconds(1),
            () => mountInfo,
            _ => counters,
            time ?? new ManualTime(),
            AppFolder,
            script ?? (() => "#!/bin/bash\necho installer\n"));
    }
}

/// <summary>
/// A <c>pkexec &lt;helper&gt; serve</c> that is not there: it plays one of the ways a start can go. <see cref="Serving"/> runs the real
/// helper loop in-process over pipes, so a broker test talks to the real protocol; the others end or stay silent.
/// </summary>
internal sealed class FakeChannel : ISessionChannel
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action _close;
    private readonly bool _endsWhenAborted;

    private FakeChannel(Stream input, Stream output, string error, Action close, bool endsWhenAborted = false)
    {
        Input = input;
        Output = output;
        Error = error;
        _close = close;
        _endsWhenAborted = endsWhenAborted;
    }

    public Stream Input { get; }

    public Stream Output { get; }

    public Task<int> Exited => _exited.Task;

    public string Error { get; }

    public bool Aborted { get; private set; }

    public void Release() => Abort();

    public void Abort()
    {
        Aborted = true;
        _close();
        if (_endsWhenAborted)
        {
            _exited.TrySetResult(143); // pkexec killed while the prompt was open; a running helper ends by itself on end of input
        }
    }

    /// <summary>The process dies by itself (kill -9, a crash): output ends and it exits.</summary>
    public void Crash()
    {
        _close();
        _exited.TrySetResult(137);
    }

    public void Dispose() => Abort();

    /// <summary>The real <see cref="HelperServe"/> behind in-memory pipes.</summary>
    public static FakeChannel Serving(HelperSeams seams)
    {
        var toHelper = new AnonymousPipeServerStream(PipeDirection.Out);
        var fromHelper = new AnonymousPipeServerStream(PipeDirection.In);
        var helperIn = new AnonymousPipeClientStream(PipeDirection.In, toHelper.ClientSafePipeHandle);
        var helperOut = new AnonymousPipeClientStream(PipeDirection.Out, fromHelper.ClientSafePipeHandle);
        FakeChannel? channel = null;
        channel = new FakeChannel(toHelper, fromHelper, "", () =>
        {
            toHelper.Dispose();
            helperOut.Dispose();
        });
        _ = Task.Run(() =>
        {
            int code = HelperServe.Run(helperIn, helperOut, new MemoryStream(), seams, CancellationToken.None);
            helperOut.Dispose();
            channel._exited.TrySetResult(code);
        });
        return channel;
    }

    /// <summary>pkexec ends at once without the helper ever running (dismissed: 126, not authorised: 127, an old helper: 2 ...).</summary>
    public static FakeChannel Exits(int code, string error = "")
    {
        var channel = new FakeChannel(Stream.Null, new MemoryStream(), error, () => { });
        channel._exited.TrySetResult(code);
        return channel;
    }

    /// <summary>The prompt stays open: nothing is answered and nothing exits until it is aborted.</summary>
    public static FakeChannel Mute()
    {
        var silent = new AnonymousPipeServerStream(PipeDirection.In);
        return new FakeChannel(Stream.Null, silent, "", silent.Dispose, endsWhenAborted: true);
    }

    /// <summary>The helper starts and says hello, then answers each later request with the given bytes (a frame is added).</summary>
    public static FakeChannel Scripted(Func<ServeRequest, byte[]?> answer)
    {
        var toHelper = new AnonymousPipeServerStream(PipeDirection.Out);
        var fromHelper = new AnonymousPipeServerStream(PipeDirection.In);
        var helperIn = new AnonymousPipeClientStream(PipeDirection.In, toHelper.ClientSafePipeHandle);
        var helperOut = new AnonymousPipeClientStream(PipeDirection.Out, fromHelper.ClientSafePipeHandle);
        var channel = new FakeChannel(toHelper, fromHelper, "", () =>
        {
            toHelper.Dispose();
            helperOut.Dispose();
        });
        _ = Task.Run(() =>
        {
            while (ServeFrames.Read(helperIn, HelperProtocol.MaxRequestBytes, out byte[] body) == FrameStatus.Frame)
            {
                ServeRequest request = JsonSerializer.Deserialize(body, HelperJson.Default.ServeRequest)!;
                if (answer(request) is not { } reply)
                {
                    return; // The scripted helper goes silent (a hang).
                }

                ServeFrames.Write(helperOut, reply);
            }

            helperOut.Dispose();
            channel._exited.TrySetResult(0);
        });
        return channel;
    }
}

/// <summary>Plays <c>Process.Start</c> of pkexec for the session: each start takes the next prepared channel; a start nobody prepared throws.</summary>
internal sealed class FakeLauncher(params Func<FakeChannel>[] starts)
{
    private readonly Queue<Func<FakeChannel>> _starts = new(starts);

    public List<FakeChannel> Channels { get; } = [];

    public List<string> Lines { get; } = [];

    public int Starts => Lines.Count;

    public ISessionChannel? Launch(string pkexec, IReadOnlyList<string> program)
    {
        Lines.Add(string.Join(' ', [pkexec, .. program]));
        FakeChannel channel = _starts.Count > 0
            ? _starts.Dequeue()()
            : throw new InvalidOperationException("Unexpected session start: " + Lines[^1]);
        Channels.Add(channel);
        return channel;
    }

    /// <summary>A launcher whose every start serves with <paramref name="seams"/>.</summary>
    public static FakeLauncher Serving(HelperSeams seams, int times = 8) =>
        new([.. Enumerable.Repeat<Func<FakeChannel>>(() => FakeChannel.Serving(seams), times)]);
}
