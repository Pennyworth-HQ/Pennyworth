using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pennyworth.Core.Privilege;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The running <c>pennyworth-helper serve</c> as the app sees it: the pipes to its standard input and output, and when it ends.
/// A test hands in a fake; production is <see cref="PkexecChannel"/>.
/// </summary>
internal interface ISessionChannel : IDisposable
{
    /// <summary>The helper's standard input: requests go here.</summary>
    Stream Input { get; }

    /// <summary>The helper's standard output: answers come from here.</summary>
    Stream Output { get; }

    /// <summary>Completes with the exit code when the process ends (pkexec's own 126 or 127 before the helper started, the helper's after).</summary>
    Task<int> Exited { get; }

    /// <summary>The start of its standard error so far (a short reason), decoded as UTF-8.</summary>
    string Error { get; }

    /// <summary>Stops it now, for a prompt that is still open or a start that failed: closes its input and kills the process (pkexec still belongs to the user then).</summary>
    void Abort();

    /// <summary>
    /// Ends a running session from this side: closes its input, which stops the helper whatever it is doing, and kills the process only
    /// if it has not exited after a moment (as root it cannot be signalled by us, and need not be).
    /// </summary>
    void Release();
}

/// <summary>Starts <c>pkexec &lt;program...&gt;</c> with all three standard streams redirected; null when it cannot be started.</summary>
internal delegate ISessionChannel? SessionLauncher(string pkexec, IReadOnlyList<string> program);

/// <summary>The real thing: <c>pkexec</c> through <see cref="Process"/>, which becomes the helper after the password.</summary>
internal sealed class PkexecChannel : ISessionChannel
{
    private readonly Process _process;
    private readonly MemoryStream _error = new();
    private readonly Task<int> _exited;
    private int _disposeScheduled;

    /// <summary>How long a released session gets to leave by itself before it is killed.</summary>
    private static readonly TimeSpan ReleaseGrace = TimeSpan.FromSeconds(3);

    private PkexecChannel(Process process)
    {
        _process = process;
        Input = process.StandardInput.BaseStream;
        Output = process.StandardOutput.BaseStream;
        Task keep = KeepStart(process.StandardError.BaseStream);
        _exited = Task.Run(async () =>
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAny(keep, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            return process.ExitCode;
        });
    }

    public Stream Input { get; }

    public Stream Output { get; }

    public Task<int> Exited => _exited;

    public string Error
    {
        get
        {
            lock (_error)
            {
                return Encoding.UTF8.GetString(_error.GetBuffer(), 0, (int)_error.Length);
            }
        }
    }

    /// <summary>Nothing but the program and its fixed arguments is passed; the environment is the app's, plus a fixed locale.</summary>
    public static ISessionChannel? Launch(string pkexec, IReadOnlyList<string> program)
    {
        var start = new ProcessStartInfo(pkexec)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["LC_ALL"] = "C" },
        };
        foreach (string argument in program)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(start) is { } process ? new PkexecChannel(process) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    public void Abort()
    {
        CloseInput();
        Kill();
        DisposeWhenExited();
    }

    public void Release()
    {
        CloseInput();
        _ = Task.Run(async () =>
        {
            if (await Task.WhenAny(_exited, Task.Delay(ReleaseGrace)).ConfigureAwait(false) != _exited)
            {
                Kill();
            }
        });
        DisposeWhenExited();
    }

    public void Dispose() => Abort();

    private void CloseInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Already closed.
        }
    }

    private void Kill()
    {
        try
        {
            _process.Kill(); // Works while pkexec still belongs to the user (before the password); refused afterwards, which is fine.
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It ended, or it is root's now.
        }
    }

    /// <summary>The process object must outlive the wait for its exit, or that wait never completes.</summary>
    private void DisposeWhenExited()
    {
        if (Interlocked.Exchange(ref _disposeScheduled, 1) == 0)
        {
            _ = _exited.ContinueWith(_ => _process.Dispose(), TaskScheduler.Default);
        }
    }

    private async Task KeepStart(Stream input)
    {
        var buffer = new byte[4 * 1024];
        int count;
        try
        {
            while ((count = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (_error)
                {
                    int room = ElevatedRunner.MaxErrorBytes - (int)_error.Length;
                    if (room > 0)
                    {
                        _error.Write(buffer, 0, Math.Min(room, count));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process is gone.
        }
    }
}

/// <summary>
/// The one long-lived root helper of a session and the rules for it: the first request starts it (one <c>pkexec</c>, one password prompt),
/// later requests reuse it, one at a time. A dismissed prompt is reported and never retried; a session that died is restarted only by
/// the next request that is allowed to ask. It locks itself after <see cref="HelperProtocol.IdleTimeout"/> without a request, on
/// <see cref="Lock"/>, and when the app closes (its end of the pipe closes, which stops the helper).
/// </summary>
internal sealed class ElevationSession : IDisposable
{
    /// <summary>Added to a request's own limit so the helper stops itself (and says why) before the app gives up on it.</summary>
    private static readonly TimeSpan DefaultMargin = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the second half of a "pkexec ended" / "the helper answered" race.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(500);

    private readonly SessionLauncher _launch;
    private readonly TimeProvider _time;
    private readonly TimeSpan _promptTimeout;
    private readonly TimeSpan _margin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private ISessionChannel? _channel;
    private ITimer? _idle;
    private long _nextId;
    private bool _disposed;

    internal ElevationSession(SessionLauncher launch, TimeProvider time, TimeSpan promptTimeout, TimeSpan? margin = null)
    {
        _launch = launch;
        _time = time;
        _promptTimeout = promptTimeout;
        _margin = margin ?? DefaultMargin;
    }

    /// <summary>A started and verified helper is running.</summary>
    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _channel is not null;
            }
        }
    }

    /// <summary>Raised, from any thread, when the session started or ended.</summary>
    public event Action? Changed;

    /// <summary>
    /// Sends <paramref name="request"/> (its <see cref="ServeRequest.Id"/> is set here) and returns the answer.
    /// <see cref="PrivilegedOutcome.Done"/> carries the helper's answer whatever its exit code says: the caller reads <see cref="ServeResponse.Exit"/>.
    /// </summary>
    /// <param name="allowStart">False: with no session running, nothing is started and the result is <see cref="PrivilegedUnavailableReason.NotActive"/>.</param>
    public async Task<PrivilegedResult<ServeResponse>> RequestAsync(string pkexec, ServeRequest request, bool allowStart, TimeSpan runTimeout, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DisarmIdle();
            if (Current() is null)
            {
                if (!allowStart)
                {
                    return PrivilegedResult<ServeResponse>.NotAvailable(PrivilegedUnavailableReason.NotActive);
                }

                if (await StartAsync(pkexec, cancellationToken).ConfigureAwait(false) is { } refused)
                {
                    return refused;
                }
            }

            return await ExchangeAsync(Current()!, request, runTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (IsActive)
            {
                ArmIdle();
            }

            _gate.Release();
        }
    }

    /// <summary>Ends the session now, even in the middle of a request (which then fails). The next request that may ask starts a new one.</summary>
    public void Lock() => End(Current());

    /// <summary>The idle timer's end: locks only if no request has begun in the meantime (one that has is not idle).</summary>
    private void LockIfIdle()
    {
        if (!_gate.Wait(0))
        {
            return;
        }

        try
        {
            Lock();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        End(Current());
        DisarmIdle();
    }

    private ISessionChannel? Current()
    {
        lock (_sync)
        {
            return _channel;
        }
    }

    /// <summary>Starts <c>pkexec &lt;helper&gt; serve</c> and waits for the helper to answer a hello; null when it is running, otherwise why it is not.</summary>
    private async Task<PrivilegedResult<ServeResponse>?> StartAsync(string pkexec, CancellationToken cancellationToken)
    {
        ISessionChannel? channel = _launch(pkexec, [HelperProtocol.InstalledPath, HelperProtocol.Serve]);
        if (channel is null)
        {
            return PrivilegedResult<ServeResponse>.NotAvailable(PrivilegedUnavailableReason.NoPkexec);
        }

        try
        {
            ServeRequest hello = new(HelperProtocol.Version, Interlocked.Increment(ref _nextId), HelperProtocol.Hello);
            Task<PrivilegedResult<ServeResponse>> exchange = ReadAnswerAsync(channel, hello, _promptTimeout, cancellationToken);
            Task<int> exited = channel.Exited;
            if (await Task.WhenAny(exchange, exited).ConfigureAwait(false) != exchange)
            {
                // pkexec ended first; an answer written just before that still counts.
                await Task.WhenAny(exchange, Task.Delay(Grace, CancellationToken.None)).ConfigureAwait(false);
            }

            PrivilegedResult<ServeResponse>? answer = exchange.IsCompleted ? await exchange.ConfigureAwait(false) : null;
            if (answer is { Outcome: PrivilegedOutcome.Done, Value: { Exit: HelperExit.Ok } })
            {
                lock (_sync)
                {
                    _channel = channel;
                }

                ISessionChannel kept = channel;
                _ = exited.ContinueWith(_ => End(kept), TaskScheduler.Default);
                channel = null; // Kept now: the finally below must not close it.
                Changed?.Invoke();
                return null;
            }

            if (answer is { Outcome: PrivilegedOutcome.Done, Value: { } refused })
            {
                return Refused(refused.Exit, refused.Error); // The helper started and said no.
            }

            // Nothing usable was answered. If pkexec has ended, its exit code says why (dismissed, not authorised, an old helper without "serve").
            if (await Task.WhenAny(exited, Task.Delay(Grace, CancellationToken.None)).ConfigureAwait(false) == exited)
            {
                return Classify(await exited.ConfigureAwait(false), channel.Error);
            }

            return answer ?? PrivilegedResult<ServeResponse>.Failed("Nobody answered in time.");
        }
        finally
        {
            channel?.Dispose();
        }
    }

    /// <summary>What a session that never started says, in the vocabulary of <see cref="PrivilegedOutcome"/>.</summary>
    internal static PrivilegedResult<ServeResponse> Classify(int exitCode, string error) => exitCode switch
    {
        126 => PrivilegedResult<ServeResponse>.Cancelled,
        127 when error.Contains("No authentication agent", StringComparison.OrdinalIgnoreCase) => PrivilegedResult<ServeResponse>.NotAvailable(PrivilegedUnavailableReason.NoAuthenticationAgent),
        127 => PrivilegedResult<ServeResponse>.Cancelled,
        HelperExit.Usage => PrivilegedResult<ServeResponse>.NotAvailable(PrivilegedUnavailableReason.HelperOutdated),
        > 127 => PrivilegedResult<ServeResponse>.Failed($"Stopped by signal {exitCode - 128}."),
        _ => PrivilegedResult<ServeResponse>.Failed(FirstLine(error) ?? $"Exited with code {exitCode}."),
    };

    private static PrivilegedResult<ServeResponse> Refused(int exit, string? error) => exit == HelperExit.Usage
        ? PrivilegedResult<ServeResponse>.NotAvailable(PrivilegedUnavailableReason.HelperOutdated)
        : PrivilegedResult<ServeResponse>.Failed(error ?? $"The helper refused to start ({exit}).");

    private async Task<PrivilegedResult<ServeResponse>> ExchangeAsync(ISessionChannel channel, ServeRequest request, TimeSpan runTimeout, CancellationToken cancellationToken)
    {
        ServeRequest numbered = request with { Id = Interlocked.Increment(ref _nextId) };
        PrivilegedResult<ServeResponse> answer;
        try
        {
            answer = await ReadAnswerAsync(channel, numbered, runTimeout + _margin, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            End(channel); // The helper cannot be told to stop one request; stopping the session stops it.
            throw;
        }

        if (answer.Outcome != PrivilegedOutcome.Done)
        {
            End(channel); // A session that timed out, broke its framing or died is not trusted with another request.
        }

        return answer;
    }

    /// <summary>Writes the request and reads its answer, ending early if the helper exits, the time runs out or the token fires.</summary>
    private static async Task<PrivilegedResult<ServeResponse>> ReadAnswerAsync(ISessionChannel channel, ServeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, HelperJson.Default.ServeRequest);
            if (body.Length > HelperProtocol.MaxRequestBytes)
            {
                return PrivilegedResult<ServeResponse>.Failed("The request is larger than the helper accepts.");
            }

            await ServeFrames.WriteAsync(channel.Input, body, limit.Token).ConfigureAwait(false);
            (FrameStatus status, byte[] payload) = await ServeFrames.ReadAsync(channel.Output, HelperProtocol.MaxResponseBytes, limit.Token).ConfigureAwait(false);
            if (status != FrameStatus.Frame)
            {
                return PrivilegedResult<ServeResponse>.Failed(status == FrameStatus.EndOfStream
                    ? FirstLine(channel.Error) ?? "The administrator session ended."
                    : "The helper's answer was cut short or too large.");
            }

            ServeResponse? response = JsonSerializer.Deserialize(payload, HelperJson.Default.ServeResponse);
            if (response is null || response.Version != HelperProtocol.Version || response.Id != request.Id)
            {
                return PrivilegedResult<ServeResponse>.Failed("The helper's answer was not the one asked for.");
            }

            return PrivilegedResult<ServeResponse>.Done(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PrivilegedResult<ServeResponse>.Failed("Nobody answered in time, or it ran too long.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException or InvalidOperationException)
        {
            return PrivilegedResult<ServeResponse>.Failed(FirstLine(channel.Error) ?? "The administrator session ended.");
        }
    }

    /// <summary>Drops <paramref name="channel"/> if it is the running one and tells the listeners.</summary>
    private void End(ISessionChannel? channel)
    {
        if (channel is null)
        {
            return;
        }

        bool wasCurrent;
        lock (_sync)
        {
            wasCurrent = ReferenceEquals(_channel, channel);
            if (wasCurrent)
            {
                _channel = null;
            }
        }

        channel.Release();
        if (wasCurrent)
        {
            DisarmIdle();
            Changed?.Invoke();
        }
    }

    private void ArmIdle()
    {
        lock (_sync)
        {
            _idle?.Dispose();
            _idle = _disposed ? null : _time.CreateTimer(_ => LockIfIdle(), null, HelperProtocol.IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisarmIdle()
    {
        lock (_sync)
        {
            _idle?.Dispose();
            _idle = null;
        }
    }

    private static string? FirstLine(string error)
    {
        string line = error.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? null : line.Length <= 300 ? line : line[..300];
    }
}
