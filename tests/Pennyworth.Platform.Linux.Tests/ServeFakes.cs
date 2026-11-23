using System.IO.Pipes;
using System.Text.Json;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>A clock the test moves by hand that also runs timers (a <see cref="CancellationTokenSource"/> with a delay is one).</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Volatile.Read(ref _now);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward and runs every timer that comes due, in order, on the calling thread.</summary>
    public void Advance(TimeSpan by)
    {
        long target = GetTimestamp() + (long)by.TotalMilliseconds;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } due && due <= target).MinBy(t => t.Due);
                if (next is null)
                {
                    Volatile.Write(ref _now, target);
                    return;
                }

                Volatile.Write(ref _now, next.Due!.Value);
                next.Fired();
            }

            next.Run();
        }
    }

    /// <summary>How many timers are armed right now.</summary>
    public int Armed
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        private long _period = -1;

        public long? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }

                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetTimestamp() + (long)dueTime.TotalMilliseconds;
                _period = period == Timeout.InfiniteTimeSpan ? -1 : (long)period.TotalMilliseconds;
            }

            return true;
        }

        public void Fired() => Due = _period > 0 ? Due + _period : null;

        public void Run() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                Due = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A <c>serve</c> session over real in-process pipes: the test is the app, the helper loop runs on its own thread.</summary>
internal sealed class ServeHarness : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly AnonymousPipeServerStream _toHelper = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _fromHelper = new(PipeDirection.In);
    private readonly AnonymousPipeClientStream _helperIn;
    private readonly AnonymousPipeClientStream _helperOut;
    private readonly MemoryStream _stderr = new();

    public ServeHarness(HelperSeams seams)
    {
        _helperIn = new AnonymousPipeClientStream(PipeDirection.In, _toHelper.ClientSafePipeHandle);
        _helperOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromHelper.ClientSafePipeHandle);
        Exit = Task.Run(() =>
        {
            try
            {
                return HelperServe.Run(_helperIn, _helperOut, _stderr, seams, CancellationToken.None);
            }
            finally
            {
                _helperOut.Dispose(); // What the end of the process does: the app's end of the pipe reads end of stream.
            }
        });
    }

    /// <summary>The helper's exit code once its loop ends.</summary>
    public Task<int> Exit { get; }

    public string Error => System.Text.Encoding.UTF8.GetString(_stderr.ToArray());

    public void SendRaw(byte[] bytes)
    {
        _toHelper.Write(bytes);
        _toHelper.Flush();
    }

    public void Send(ServeRequest request) =>
        ServeFrames.Write(_toHelper, JsonSerializer.SerializeToUtf8Bytes(request, HelperJson.Default.ServeRequest));

    public void SendFrame(byte[] payload) => ServeFrames.Write(_toHelper, payload);

    /// <summary>Closes the helper's standard input: what the app's exit or lock does.</summary>
    public void CloseInput() => _toHelper.Dispose();

    public ServeResponse Receive() => JsonSerializer.Deserialize(ReceiveFrame(), HelperJson.Default.ServeResponse)!;

    public byte[] ReceiveFrame()
    {
        Task<(FrameStatus Status, byte[] Payload)> read = Task.Run(() =>
        {
            FrameStatus status = ServeFrames.Read(_fromHelper, HelperProtocol.MaxResponseBytes, out byte[] payload);
            return (status, payload);
        });
        (FrameStatus status, byte[] payload) = read.WaitAsync(Patience).GetAwaiter().GetResult();
        Assert.Equal(FrameStatus.Frame, status);
        return payload;
    }

    /// <summary>Whether the helper wrote nothing and closed its output (it is gone).</summary>
    public FrameStatus ReceiveEnd()
    {
        Task<FrameStatus> read = Task.Run(() => ServeFrames.Read(_fromHelper, HelperProtocol.MaxResponseBytes, out _));
        return read.WaitAsync(Patience).GetAwaiter().GetResult();
    }

    public ServeResponse Ask(string op, string? argument = null, IReadOnlyList<string>? paths = null, IReadOnlyList<CleanFile>? files = null, long id = 1)
    {
        Send(new ServeRequest(HelperProtocol.Version, id, op, argument, paths, files));
        return Receive();
    }

    public int WaitForExit() => Exit.WaitAsync(Patience).GetAwaiter().GetResult();

    public void Dispose()
    {
        _toHelper.Dispose();
        _fromHelper.Dispose();
        _helperIn.Dispose();
        _helperOut.Dispose();
    }
}

internal static class ServeFakes
{
    public const string MountInfo = "45 1 0:41 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n";

    /// <summary>No peer rules: what a test that is not about the peer check runs with.</summary>
    public static PeerSources NoPeer { get; } = new(() => 4242, _ => 1000, () => true);

    public static CallerSources Caller(uint euid = 0, string? pkexecUid = "1000") => new(() => euid, () => pkexecUid, _ => true, () => null);

    public static HelperSeams Seams(
        ManualTime? time = null,
        CallerSources? caller = null,
        PeerSources? peer = null,
        Func<CancellationToken, FirmwareTablesResponse>? firmware = null,
        Func<string, CancellationToken, SystemSpaceResponse>? space = null,
        Func<FoldersRequest, CancellationToken, FoldersResponse>? folders = null,
        Func<string, CleanRequest, CancellationToken, CleanResponse>? clean = null,
        Action<int>? terminate = null) =>
        new(
            caller ?? Caller(),
            () => MountInfo,
            firmware ?? (_ => new FirmwareTablesResponse(HelperProtocol.Version, "AAAA")),
            space ?? ((_, _) => new SystemSpaceResponse(HelperProtocol.Version, "ext4", 11, 22)),
            folders ?? ((request, _) => new FoldersResponse(HelperProtocol.Version, [.. request.Paths.Select(p => new FolderSize(p, 1, 2, 3, true))])),
            clean ?? ((_, _, _) => new CleanResponse(HelperProtocol.Version, 5, 1, 0)),
            peer ?? NoPeer,
            time ?? new ManualTime(),
            terminate ?? (_ => { }));
}
