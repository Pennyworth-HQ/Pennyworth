using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// What the four subcommands run and what the entry point reads from the machine. <see cref="Real"/> is production; tests
/// replace single members to drive every exit code without touching the system.
/// </summary>
/// <param name="Caller">Who runs the helper; see <see cref="CallerSources"/>.</param>
/// <param name="ReadMountInfo">The text of <c>/proc/self/mountinfo</c>, empty when unreadable.</param>
/// <param name="ReadFirmwareTables"><c>read-firmware-tables</c>.</param>
/// <param name="MeasureSystemSpace"><c>measure-system-space</c>, given a mount point the dispatcher has validated.</param>
/// <param name="MeasureFolders"><c>measure-folders</c>, given a request whose shape the dispatcher has validated.</param>
/// <param name="CleanSystem"><c>clean-system</c>, given a known rule id and a request whose shape the dispatcher has validated; only ever called with root rights.</param>
/// <param name="Peer">What <c>serve</c> learns about the process that started it; see <see cref="PeerSources"/>.</param>
/// <param name="Time">The clock <c>serve</c> measures idle time, request time and its parent check by; a test hands in its own.</param>
/// <param name="Terminate">What <c>serve</c> calls, with an exit code, when a request does not stop at its time limit although it was asked to.</param>
internal sealed record HelperSeams(
    CallerSources Caller,
    Func<string> ReadMountInfo,
    Func<CancellationToken, FirmwareTablesResponse> ReadFirmwareTables,
    Func<string, CancellationToken, SystemSpaceResponse> MeasureSystemSpace,
    Func<FoldersRequest, CancellationToken, FoldersResponse> MeasureFolders,
    Func<string, CleanRequest, CancellationToken, CleanResponse> CleanSystem,
    PeerSources Peer,
    TimeProvider Time,
    Action<int> Terminate)
{
    internal static HelperSeams Real { get; } = new(
        CallerSources.Real,
        MountTable.ReadSystem,
        FirmwareTables.Read,
        SystemSpace.Run,
        FolderMeasure.Run,
        SystemCleanup.Run,
        PeerSources.Real,
        TimeProvider.System,
        static code => Environment.Exit(code));
}

/// <summary>
/// The root helper's whole command line: one fixed subcommand, at most one validated argument, a JSON request on stdin where the
/// subcommand takes one, a JSON answer on stdout (or, for <c>serve</c>, many of both: see <see cref="HelperServe"/>). Nothing but <c>PKEXEC_UID</c> is read from the environment, nothing is passed to a
/// shell, and every failure ends as one line on stderr plus an exit code of <see cref="HelperExit"/>, never a stack trace and never
/// the content of a file.
/// </summary>
internal static class HelperApp
{
    internal const string ErrorPrefix = "pennyworth-helper: ";
    private const int MaxMountPointChars = 4096;

    /// <summary>Runs the helper with the real machine; the exit code is the process's.</summary>
    internal static int Run(string[] args, Stream stdin, Stream stdout, Stream stderr, CancellationToken cancellationToken) =>
        Run(args, stdin, stdout, stderr, HelperSeams.Real, cancellationToken);

    internal static int Run(string[] args, Stream stdin, Stream stdout, Stream stderr, HelperSeams seams, CancellationToken cancellationToken)
    {
        if (args is [HelperProtocol.Serve])
        {
            return HelperServe.Run(stdin, stdout, stderr, seams, cancellationToken);
        }

        int exitCode;
        string message;
        try
        {
            byte[] answer = Execute(args, stdin, seams, cancellationToken);
            if (answer.Length > HelperProtocol.MaxResponseBytes)
            {
                throw new HelperException(HelperExit.TooLarge, "the answer is larger than the allowed size");
            }

            stdout.Write(answer);
            stdout.Flush();
            return HelperExit.Ok;
        }
        catch (HelperException ex)
        {
            // 126 and 127 belong to pkexec; a code outside the table is a bug in the helper, reported as a plain failure.
            exitCode = ex.ExitCode is >= HelperExit.Failed and <= HelperExit.Missing ? ex.ExitCode : HelperExit.Failed;
            message = ex.Message;
        }
        catch (OperationCanceledException)
        {
            exitCode = HelperExit.TimedOut;
            message = "stopped at the time limit";
        }
        catch (JsonException)
        {
            exitCode = HelperExit.Usage;
            message = "the request is not valid";
        }
        catch (Exception)
        {
            // Deliberately nothing about the cause: an exception text can carry a path or file content.
            exitCode = HelperExit.Failed;
            message = "unexpected failure";
        }

        WriteError(stderr, message);
        return exitCode;
    }

    private static byte[] Execute(string[] args, Stream stdin, HelperSeams seams, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw Usage("a subcommand is required");
        }

        string subcommand = args[0];
        if (!HelperProtocol.Subcommands.Contains(subcommand) || subcommand == HelperProtocol.Serve)
        {
            throw Usage("unknown subcommand"); // "serve" has its own entry above and takes no arguments.
        }

        int argumentCount = subcommand is HelperProtocol.MeasureSystemSpace or HelperProtocol.CleanSystem ? 1 : 0;
        if (args.Length - 1 != argumentCount)
        {
            throw Usage("wrong number of arguments");
        }

        Caller caller = CallerCheck.Verify(seams.Caller);
        switch (subcommand)
        {
            case HelperProtocol.ReadFirmwareTables:
                return Serialize(HelperOperations.ReadFirmwareTables(seams, cancellationToken), HelperJson.Default.FirmwareTablesResponse);

            case HelperProtocol.MeasureSystemSpace:
                return Serialize(HelperOperations.MeasureSystemSpace(seams, args[1], cancellationToken), HelperJson.Default.SystemSpaceResponse);

            case HelperProtocol.MeasureFolders:
                FoldersRequest folders = ReadRequest(stdin, HelperJson.Default.FoldersRequest, cancellationToken);
                return Serialize(HelperOperations.MeasureFolders(seams, folders, cancellationToken), HelperJson.Default.FoldersResponse);

            default:
                // The rule is judged before the request is read: a caller that may not clean is not even listened to.
                HelperOperations.RequireCleaning(caller, args[1]);
                CleanRequest clean = ReadRequest(stdin, HelperJson.Default.CleanRequest, cancellationToken);
                return Serialize(HelperOperations.CleanSystem(seams, caller, args[1], clean, cancellationToken), HelperJson.Default.CleanResponse);
        }
    }

    /// <summary>
    /// A mount point is accepted only as the exact text of one in <c>/proc/self/mountinfo</c>: absolute, no empty, <c>.</c> or
    /// <c>..</c> segment, no trailing separator (except <c>/</c>), no NUL. The helper never normalises what it was given, so
    /// what it measures is what the kernel lists.
    /// </summary>
    internal static string ValidateMountPoint(string argument, string mountInfo)
    {
        if (argument.Length is 0 or > MaxMountPointChars || argument[0] != '/' || argument.Contains('\0'))
        {
            throw new HelperException(HelperExit.Refused, "the mount point must be an absolute path");
        }

        if (argument != "/")
        {
            foreach (string segment in argument.Split('/').Skip(1))
            {
                if (segment.Length == 0 || segment is "." or "..")
                {
                    throw new HelperException(HelperExit.Refused, "the mount point must be a normalised path");
                }
            }
        }

        if (!MountTable.Parse(mountInfo).Any(mount => mount.MountPoint == argument))
        {
            throw new HelperException(HelperExit.Refused, "the path is not a mount point");
        }

        return argument;
    }

    private static T ReadRequest<T>(Stream stdin, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        using var body = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stdin.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }

            if (body.Length + read > HelperProtocol.MaxRequestBytes)
            {
                throw new HelperException(HelperExit.TooLarge, "the request is larger than the allowed size");
            }

            body.Write(chunk, 0, read);
        }

        return JsonSerializer.Deserialize(body.GetBuffer().AsSpan(0, (int)body.Length), typeInfo)
            ?? throw Usage("the request is empty");
    }

    private static byte[] Serialize<T>(T answer, JsonTypeInfo<T> typeInfo) => JsonSerializer.SerializeToUtf8Bytes(answer, typeInfo);

    internal static HelperException Usage(string message) => new(HelperExit.Usage, message);

    internal static void WriteError(Stream stderr, string message)
    {
        string line = ErrorPrefix + message.ReplaceLineEndings(" ") + "\n";
        try
        {
            stderr.Write(Encoding.UTF8.GetBytes(line));
            stderr.Flush();
        }
        catch (IOException)
        {
            // Nobody is reading stderr; the exit code still says what happened.
        }
    }
}
