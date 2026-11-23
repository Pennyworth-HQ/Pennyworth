using System.Text.Json.Serialization;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// What the app and the root helper agree on: where the helper lives, the subcommands (each also a polkit
/// action), the exit codes and the JSON that crosses stdin and stdout. Both sides compile this one file, so they
/// cannot drift. Nothing here is a secret and nothing in a request is trusted by the helper: it validates every field.
/// </summary>
public static class HelperProtocol
{
    /// <summary>The only place the app will run the helper from (and the path the polkit policy names).</summary>
    public const string InstalledPath = "/usr/libexec/pennyworth/pennyworth-helper";

    /// <summary>The JSON <c>version</c> both sides write and expect; a helper that answers another version is outdated.</summary>
    public const int Version = 1;

    /// <summary>The longest stdin the helper reads; anything beyond is refused with <see cref="HelperExit.TooLarge"/>.</summary>
    public const int MaxRequestBytes = 32 * 1024 * 1024;

    /// <summary>The longest stdout the app reads from the helper.</summary>
    public const int MaxResponseBytes = 8 * 1024 * 1024;

    /// <summary>Most folders one <c>measure-folders</c> request may name.</summary>
    public const int MaxFolders = 512;

    /// <summary>Most files one <c>clean-system</c> request may list; the app sends no more and counts the rest as skipped.</summary>
    public const int MaxCleanFiles = 200_000;

    /// <summary>Longest a single helper run (or one request of a <see cref="Serve"/> session) may work before it stops itself.</summary>
    public static readonly TimeSpan MaxRuntime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a <see cref="Serve"/> session may sit without a request before it ends: the helper stops itself after this
    /// and the app locks its side at the same time, so the next privileged action asks for the password again. The polkit
    /// policy of <see cref="Serve"/> names this number in its prompt (a test keeps them in step).
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>How often a session checks that the process that started it is still its parent.</summary>
    public static readonly TimeSpan ParentCheckInterval = TimeSpan.FromSeconds(1);

    public const string ReadFirmwareTables = "read-firmware-tables";
    public const string MeasureSystemSpace = "measure-system-space";
    public const string MeasureFolders = "measure-folders";
    public const string CleanSystem = "clean-system";

    /// <summary>
    /// The long-lived mode: one root process, started by one <c>pkexec</c>, answers framed requests (<see cref="ServeFrames"/>) on
    /// stdin and stdout for every operation above until stdin closes, <see cref="IdleTimeout"/> passes without a request or its
    /// parent goes away. It offers exactly the four operations above and nothing else (<see cref="ServeRequest.Op"/>), plus <see cref="Hello"/>.
    /// </summary>
    public const string Serve = "serve";

    /// <summary>The one request of a session that does nothing: it proves the helper speaks this <see cref="Version"/> and is ready.</summary>
    public const string Hello = "hello";

    /// <summary>The polkit action ids of <c>app.pennyworth.policy</c>, one per subcommand.</summary>
    public static string ActionId(string subcommand) => "app.pennyworth." + subcommand;

    /// <summary>The subcommands, in the order the policy lists them.</summary>
    public static IReadOnlyList<string> Subcommands { get; } = [ReadFirmwareTables, MeasureSystemSpace, MeasureFolders, CleanSystem, Serve];

    /// <summary>The file the firmware tables are read from; the helper reads nothing else for <see cref="ReadFirmwareTables"/>.</summary>
    public const string FirmwareTablePath = "/sys/firmware/dmi/tables/DMI";

    /// <summary>Largest firmware table the helper returns (the real one is tens of kilobytes).</summary>
    public const int MaxFirmwareTableBytes = 4 * 1024 * 1024;
}

/// <summary>
/// Exit codes of the helper. 126 and 127 are pkexec's own ("dismissed" / "not authorised") and are never used here.
/// 0 is success; the others say why nothing was done. The message is on stderr, one line.
/// </summary>
public static class HelperExit
{
    public const int Ok = 0;

    /// <summary>An unexpected failure inside the helper.</summary>
    public const int Failed = 1;

    /// <summary>Unknown subcommand, wrong argument count or unreadable JSON: the caller did not follow the protocol.</summary>
    public const int Usage = 2;

    /// <summary>The request was understood and refused: invalid path, caller not a normal user, an allowlisted folder that is not safe.</summary>
    public const int Refused = 3;

    /// <summary>The request or an answer is larger than the fixed limits.</summary>
    public const int TooLarge = 4;

    /// <summary>The run was stopped at <see cref="HelperProtocol.MaxRuntime"/>.</summary>
    public const int TimedOut = 5;

    /// <summary>A system tool or file the action needs is not there.</summary>
    public const int Missing = 6;
}

/// <summary>Stops a helper action with one of the <see cref="HelperExit"/> codes; the message becomes the one line on stderr. Carries no secrets and no file content.</summary>
public sealed class HelperException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Answer of <c>read-firmware-tables</c>.</summary>
/// <param name="Table"><c>/sys/firmware/dmi/tables/DMI</c>, base64.</param>
public sealed record FirmwareTablesResponse(int Version, string Table);

/// <summary>Answer of <c>measure-system-space &lt;mountpoint&gt;</c>. A part the helper could not measure is null.</summary>
/// <param name="SnapshotBytes">Bytes only snapshots hold: what deleting every snapshot would free.</param>
/// <param name="MetadataBytes">Reserved blocks plus, on btrfs, the metadata and system chunks in use; the same quantity the unprivileged measurement reports.</param>
public sealed record SystemSpaceResponse(int Version, string FileSystem, long? SnapshotBytes, long? MetadataBytes);

/// <summary>Request of <c>measure-folders</c>, on stdin.</summary>
public sealed record FoldersRequest(int Version, IReadOnlyList<string> Paths);

/// <summary>One folder of a <see cref="FoldersResponse"/>, counted the way the scan counts (allocated bytes, hard links once, no other file system).</summary>
public sealed record FolderSize(string Path, long SizeOnDisk, long LogicalSize, long Files, bool Complete);

/// <summary>Answer of <c>measure-folders</c>: one entry per accepted path, in request order. A refused path is left out.</summary>
public sealed record FoldersResponse(int Version, IReadOnlyList<FolderSize> Folders);

/// <summary>One file of the preview the person saw, as <c>clean-system</c> receives it.</summary>
/// <param name="Root">One of the rule's allowlisted folders (<see cref="SystemCleanupRules"/>), exactly as listed there.</param>
/// <param name="Path">Path below <paramref name="Root"/>, <c>/</c>-separated, never absolute, never containing <c>..</c>.</param>
/// <param name="Size">Size listed in the preview; reported as freed when the file is deleted.</param>
/// <param name="ModifiedTicks">Last write time when listed: UTC <see cref="DateTime.Ticks"/> (100 ns since 0001-01-01), what .NET reports. A file whose time differs now is left alone.</param>
public sealed record CleanFile(string Root, string Path, long Size, long ModifiedTicks);

/// <summary>Request of <c>clean-system &lt;ruleId&gt;</c>, on stdin: the preview, nothing else. The helper deletes only what it lists in its own allowlist AND the preview names.</summary>
public sealed record CleanRequest(int Version, IReadOnlyList<CleanFile> Files);

/// <summary>Answer of <c>clean-system</c>.</summary>
public sealed record CleanResponse(int Version, long FreedBytes, long Deleted, long Skipped);

/// <summary>
/// One request of a <see cref="HelperProtocol.Serve"/> session, as a frame body (JSON). The fields beyond <paramref name="Op"/>
/// are the ones that operation takes; the helper validates all of them exactly as the one-shot subcommand of the same name does,
/// and refuses (without ending the session) any <paramref name="Op"/> it does not know.
/// </summary>
/// <param name="Id">The caller's number for this request; the answer carries it back.</param>
/// <param name="Op">One of <see cref="HelperProtocol.ReadFirmwareTables"/>, <see cref="HelperProtocol.MeasureSystemSpace"/>, <see cref="HelperProtocol.MeasureFolders"/>, <see cref="HelperProtocol.CleanSystem"/>, <see cref="HelperProtocol.Hello"/>.</param>
/// <param name="Argument">The mount point of <c>measure-system-space</c>, the rule id of <c>clean-system</c>.</param>
/// <param name="Paths">The folders of <c>measure-folders</c>.</param>
/// <param name="Files">The previewed files of <c>clean-system</c>.</param>
public sealed record ServeRequest(int Version, long Id, string Op, string? Argument = null, IReadOnlyList<string>? Paths = null, IReadOnlyList<CleanFile>? Files = null);

/// <summary>
/// The answer to one <see cref="ServeRequest"/>, as a frame body. <paramref name="Exit"/> is what the one-shot subcommand would have
/// exited with (<see cref="HelperExit"/>): 0 with the matching result filled in, otherwise <paramref name="Error"/> says why in one line.
/// </summary>
/// <param name="Id">The <see cref="ServeRequest.Id"/> answered; 0 when the request could not even be read.</param>
public sealed record ServeResponse(
    int Version,
    long Id,
    int Exit,
    string? Error = null,
    FirmwareTablesResponse? Firmware = null,
    SystemSpaceResponse? Space = null,
    FoldersResponse? Folders = null,
    CleanResponse? Clean = null);

/// <summary>How <see cref="ServeFrames.Read"/> ended.</summary>
public enum FrameStatus
{
    /// <summary>A whole frame was read.</summary>
    Frame,

    /// <summary>The stream ended cleanly between two frames.</summary>
    EndOfStream,

    /// <summary>The announced length is larger than the limit: nothing of the body was read, and the stream cannot be followed any more.</summary>
    TooLarge,

    /// <summary>The stream ended inside a frame.</summary>
    Truncated,
}

/// <summary>
/// The framing of a session: a 4-byte big-endian length, then that many bytes of UTF-8 JSON. Both directions, one frame per request
/// and per answer, answers in request order. Length first means the reader never buffers more than it was told it would
/// get, and can refuse a frame by its announced size alone.
/// </summary>
public static class ServeFrames
{
    public const int HeaderBytes = 4;

    /// <summary>Writes <paramref name="payload"/> as one frame and flushes.</summary>
    public static void Write(Stream stream, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    /// <summary>Async twin of <see cref="Write"/>; the frame goes out in one piece so a reader never sees half of it from two writers.</summary>
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[HeaderBytes + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsMemory(HeaderBytes));
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame of at most <paramref name="maxBytes"/>.</summary>
    public static FrameStatus Read(Stream stream, int maxBytes, out byte[] payload)
    {
        payload = [];
        Span<byte> header = stackalloc byte[HeaderBytes];
        int got = stream.ReadAtLeast(header, HeaderBytes, throwOnEndOfStream: false);
        if (got == 0)
        {
            return FrameStatus.EndOfStream;
        }

        if (got < HeaderBytes)
        {
            return FrameStatus.Truncated;
        }

        uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > (uint)maxBytes)
        {
            return FrameStatus.TooLarge;
        }

        byte[] body = new byte[length];
        if (stream.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) < body.Length)
        {
            return FrameStatus.Truncated;
        }

        payload = body;
        return FrameStatus.Frame;
    }

    /// <summary>Async twin of <see cref="Read"/>.</summary>
    public static async Task<(FrameStatus Status, byte[] Payload)> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderBytes];
        int got = await stream.ReadAtLeastAsync(header, HeaderBytes, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (got == 0)
        {
            return (FrameStatus.EndOfStream, []);
        }

        if (got < HeaderBytes)
        {
            return (FrameStatus.Truncated, []);
        }

        uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > (uint)maxBytes)
        {
            return (FrameStatus.TooLarge, []);
        }

        byte[] body = new byte[length];
        int read = await stream.ReadAtLeastAsync(body, body.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return read < body.Length ? (FrameStatus.Truncated, []) : (FrameStatus.Frame, body);
    }
}

/// <summary>
/// The rules the helper may clean and the folders it cleans for each, compiled in: the caller names a rule, never a path to
/// delete. The folders are those of the knowledge base's rules that have <c>requiresAdmin</c> (a test keeps both lists equal).
/// </summary>
public static class SystemCleanupRules
{
    private static readonly Dictionary<string, string[]> Folders = new(StringComparer.Ordinal)
    {
        ["system-package-cache"] =
        [
            "/var/cache/zypp/packages",
            "/var/cache/apt/archives",
            "/var/cache/pacman/pkg",
            "/var/cache/dnf",
            "/var/cache/libdnf5",
        ],
        ["system-crash-reports"] = ["/var/crash"],
    };

    /// <summary>The rule ids, for the policy test and the helper's usage text.</summary>
    public static IReadOnlyCollection<string> Ids => Folders.Keys;

    /// <summary>The allowlisted folders of <paramref name="ruleId"/>; null for any rule the helper does not know.</summary>
    public static IReadOnlyList<string>? FoldersOf(string ruleId) => Folders.GetValueOrDefault(ruleId);

    /// <summary>
    /// Names inside a rule's folder the helper never deletes even when the preview lists them: <c>lock</c> and <c>partial</c>
    /// in apt's archive (apt's own working files). Compared against the first path segment below the folder.
    /// </summary>
    public static bool IsKept(string root, string relativePath)
    {
        if (root != "/var/cache/apt/archives")
        {
            return false;
        }

        int slash = relativePath.IndexOf('/', StringComparison.Ordinal);
        string first = slash < 0 ? relativePath : relativePath[..slash];
        return first is "lock" or "partial";
    }
}

[JsonSerializable(typeof(FirmwareTablesResponse))]
[JsonSerializable(typeof(SystemSpaceResponse))]
[JsonSerializable(typeof(FoldersRequest))]
[JsonSerializable(typeof(FoldersResponse))]
[JsonSerializable(typeof(CleanRequest))]
[JsonSerializable(typeof(CleanResponse))]
[JsonSerializable(typeof(ServeRequest))]
[JsonSerializable(typeof(ServeResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
public sealed partial class HelperJson : JsonSerializerContext;
