using System.Collections.Concurrent;
using System.Text.Json;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// <c>pennyworth-helper serve</c>: the long-lived mode that lets one <c>pkexec</c> password prompt cover a whole session.
/// The process reads framed <see cref="ServeRequest"/>s from standard input (<see cref="ServeFrames"/>), runs each through
/// <see cref="HelperOperations"/>, the very code the one-shot subcommands run, and writes one framed <see cref="ServeResponse"/> per
/// request, in order, one at a time. It has no other way in: no socket, file or FIFO exists for anyone else to connect to,
/// and it serves only the pipe it was started with, after <see cref="CallerCheck"/> and <see cref="PeerCheck"/> accepted
/// who started it.
/// </summary>
/// <remarks>
/// It ends, with exit code 0, when standard input closes (the app exited or locked), when no request arrived for
/// <see cref="HelperProtocol.IdleTimeout"/>, or when its parent process changes (the app died). A request that is running when that
/// happens is cancelled, and if it does not stop within <see cref="HardStopGrace"/> the process terminates itself. Each request has
/// <see cref="HelperProtocol.MaxRuntime"/>, the same as a one-shot run. A request that cannot be understood, names an unknown
/// operation or fails validation gets an error answer and the session goes on; only a broken frame (announced larger than
/// <see cref="HelperProtocol.MaxRequestBytes"/>, or cut short) ends it, because the stream cannot be followed after that.
/// </remarks>
internal static class HelperServe
{
    /// <summary>How long a cancelled request may take to stop before the whole process is ended instead.</summary>
    internal static readonly TimeSpan HardStopGrace = TimeSpan.FromSeconds(10);

    /// <summary>Requests read ahead of the one being run; the app sends one at a time, so this only bounds a misbehaving peer.</summary>
    private const int ReadAhead = 2;

    internal static int Run(Stream stdin, Stream stdout, Stream stderr, HelperSeams seams, CancellationToken stop)
    {
        Caller caller;
        int? parent;
        try
        {
            caller = CallerCheck.Verify(seams.Caller);
            parent = PeerCheck.Verify(seams.Peer, caller);
        }
        catch (HelperException ex)
        {
            HelperApp.WriteError(stderr, ex.Message);
            return ex.ExitCode;
        }

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var incoming = new BlockingCollection<(FrameStatus Status, byte[] Payload)>(ReadAhead);
        int running = 0;
        ITimer? hardStop = null;

        void Shutdown()
        {
            if (shutdown.IsCancellationRequested)
            {
                return;
            }

            // A request that ignores its cancellation (a file system that hangs) must not keep a root process alive.
            hardStop = seams.Time.CreateTimer(_ => seams.Terminate(HelperExit.TimedOut), null, HardStopGrace, Timeout.InfiniteTimeSpan);
            shutdown.Cancel();
        }

        var reader = new Thread(() => ReadFrames(stdin, incoming, shutdown.Token, () =>
        {
            if (Volatile.Read(ref running) == 1)
            {
                Shutdown(); // The peer left in the middle of a request: nobody wants the answer.
            }
        }))
        {
            IsBackground = true,
            Name = "pennyworth-serve-reader",
        };
        reader.Start();

        using ITimer? parentWatch = parent is { } expected
            ? seams.Time.CreateTimer(
                _ =>
                {
                    if (!PeerCheck.ParentUnchanged(seams.Peer, expected))
                    {
                        Shutdown();
                    }
                },
                null,
                HelperProtocol.ParentCheckInterval,
                HelperProtocol.ParentCheckInterval)
            : null;

        try
        {
            while (true)
            {
                using var idle = new CancellationTokenSource(HelperProtocol.IdleTimeout, seams.Time);
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(idle.Token, shutdown.Token);
                (FrameStatus status, byte[] payload) item;
                try
                {
                    item = incoming.Take(waiting.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!shutdown.IsCancellationRequested)
                    {
                        HelperApp.WriteError(stderr, "no request for " + (int)HelperProtocol.IdleTimeout.TotalMinutes + " minutes; ending the session");
                    }

                    return HelperExit.Ok;
                }

                switch (item.status)
                {
                    case FrameStatus.Frame:
                        Volatile.Write(ref running, 1);
                        byte[]? answer;
                        try
                        {
                            answer = Answer(seams, caller, item.payload, shutdown.Token);
                        }
                        finally
                        {
                            Volatile.Write(ref running, 0);
                        }

                        if (answer is null || !TryWrite(stdout, answer))
                        {
                            return HelperExit.Ok; // Cancelled because the peer left, or the peer is gone.
                        }

                        break;

                    case FrameStatus.EndOfStream:
                        return HelperExit.Ok;

                    case FrameStatus.TooLarge:
                        TryWrite(stdout, Failure(0, HelperExit.TooLarge, "the request is larger than the allowed size"));
                        HelperApp.WriteError(stderr, "a frame is larger than the allowed size; ending the session");
                        return HelperExit.TooLarge;

                    default:
                        HelperApp.WriteError(stderr, "a frame was cut short; ending the session");
                        return HelperExit.Usage;
                }
            }
        }
        finally
        {
            hardStop?.Dispose();
        }
    }

    private static void ReadFrames(Stream stdin, BlockingCollection<(FrameStatus Status, byte[] Payload)> incoming, CancellationToken stop, Action onEnd)
    {
        try
        {
            while (true)
            {
                FrameStatus status;
                byte[] payload;
                try
                {
                    status = ServeFrames.Read(stdin, HelperProtocol.MaxRequestBytes, out payload);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    status = FrameStatus.EndOfStream;
                    payload = [];
                }

                incoming.Add((status, payload), stop);
                if (status != FrameStatus.Frame)
                {
                    onEnd();
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The session ended first.
        }
    }

    /// <summary>The framed answer to one request; null when the request was cancelled because the session is ending.</summary>
    private static byte[]? Answer(HelperSeams seams, Caller caller, byte[] frame, CancellationToken shutdown)
    {
        ServeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(frame, HelperJson.Default.ServeRequest);
        }
        catch (JsonException)
        {
            return Failure(0, HelperExit.Usage, "the request is not valid");
        }

        if (request?.Op is null)
        {
            return Failure(0, HelperExit.Usage, "the request is incomplete");
        }

        using var runtime = new CancellationTokenSource(HelperProtocol.MaxRuntime, seams.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, runtime.Token);
        try
        {
            HelperOperations.RequireVersion(request.Version);
            ServeResponse ok = Execute(seams, caller, request, linked.Token);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ok, HelperJson.Default.ServeResponse);
            return bytes.Length > HelperProtocol.MaxResponseBytes
                ? Failure(request.Id, HelperExit.TooLarge, "the answer is larger than the allowed size")
                : bytes;
        }
        catch (HelperException ex)
        {
            int exit = ex.ExitCode is >= HelperExit.Failed and <= HelperExit.Missing ? ex.ExitCode : HelperExit.Failed;
            return Failure(request.Id, exit, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return shutdown.IsCancellationRequested ? null : Failure(request.Id, HelperExit.TimedOut, "stopped at the time limit");
        }
        catch (JsonException)
        {
            return Failure(request.Id, HelperExit.Usage, "the request is not valid");
        }
        catch (Exception)
        {
            // Deliberately nothing about the cause: an exception text can carry a path or file content.
            return Failure(request.Id, HelperExit.Failed, "unexpected failure");
        }
    }

    private static ServeResponse Execute(HelperSeams seams, Caller caller, ServeRequest request, CancellationToken cancellationToken)
    {
        long id = request.Id;
        int version = HelperProtocol.Version;
        switch (request.Op)
        {
            case HelperProtocol.Hello:
                return new ServeResponse(version, id, HelperExit.Ok);

            case HelperProtocol.ReadFirmwareTables:
                return new ServeResponse(version, id, HelperExit.Ok, Firmware: HelperOperations.ReadFirmwareTables(seams, cancellationToken));

            case HelperProtocol.MeasureSystemSpace:
                string mountPoint = request.Argument ?? throw HelperApp.Usage("a mount point is required");
                return new ServeResponse(version, id, HelperExit.Ok, Space: HelperOperations.MeasureSystemSpace(seams, mountPoint, cancellationToken));

            case HelperProtocol.MeasureFolders:
                var folders = new FoldersRequest(request.Version, request.Paths ?? throw HelperApp.Usage("the request is incomplete"));
                return new ServeResponse(version, id, HelperExit.Ok, Folders: HelperOperations.MeasureFolders(seams, folders, cancellationToken));

            case HelperProtocol.CleanSystem:
                string ruleId = request.Argument ?? throw HelperApp.Usage("a cleanup rule is required");
                HelperOperations.RequireCleaning(caller, ruleId);
                var clean = new CleanRequest(request.Version, request.Files ?? throw HelperApp.Usage("the request is incomplete"));
                return new ServeResponse(version, id, HelperExit.Ok, Clean: HelperOperations.CleanSystem(seams, caller, ruleId, clean, cancellationToken));

            default:
                throw HelperApp.Usage("unknown operation");
        }
    }

    private static byte[] Failure(long id, int exit, string message) =>
        JsonSerializer.SerializeToUtf8Bytes(new ServeResponse(HelperProtocol.Version, id, exit, message), HelperJson.Default.ServeResponse);

    /// <summary>Writes one framed answer; false when the other end is gone.</summary>
    private static bool TryWrite(Stream stdout, byte[] answer)
    {
        try
        {
            ServeFrames.Write(stdout, answer);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
    }
}
