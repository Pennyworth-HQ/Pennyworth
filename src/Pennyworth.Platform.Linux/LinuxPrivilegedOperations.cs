using System.Text;
using System.Text.Json;
using Pennyworth.Cleanup;
using Pennyworth.Core.Privilege;
using Pennyworth.Scanning;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The few things Pennyworth does with administrator rights on Linux, each only when a person pressed the button that
/// says so. The system's polkit agent shows the password dialog and owns the password: it never passes through the app,
/// its arguments, its environment or its input. Only <c>pkexec</c> is used, never <c>sudo</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two routes, chosen on every call (nothing is remembered, so a helper that is installed or tampered with later is noticed):
/// </para>
/// <list type="bullet">
/// <item><description>
/// The helper's session (<see cref="LinuxAdministratorAccess"/>): one <c>pkexec &lt;helper&gt; serve</c>, started by the first request
/// that may ask and reused by every later one, only when <see cref="HelperTrust"/> finds the helper and every folder above it owned
/// by root and unwritable by others. Everything goes through it when it can: reading the firmware table and the system space, and
/// the two features that go beyond reading, <see cref="PrivilegedOperation.Folders"/> and <see cref="PrivilegedOperation.SystemCleanup"/>.
/// </description></item>
/// <item><description>
/// Without a usable helper, the two features that only read fall back to <c>pkexec</c> of fixed system tools found in fixed
/// folders (<c>cat</c> of the firmware table, <c>dmidecode</c>, <c>btrfs</c>), which the polkit agent presents as a generic
/// "run this program as root" and which are one-off: each asks again and none starts the session. Deleting and measuring folders
/// never fall back: nothing arbitrary is ever run as root, and those two say they need the setup
/// (<see cref="IAdministratorAccess.SetupAsync"/>).
/// </description></item>
/// </list>
/// <para>
/// A dismissed or refused prompt (pkexec exits 126 or 127) settles an operation; it is never asked again.
/// Cancelling the token kills <c>pkexec</c>, which closes the dialog.
/// </para>
/// </remarks>
internal sealed class LinuxPrivilegedOperations : IPrivilegedOperations
{
    /// <summary>Long enough for a person to find the password, short enough to end if the prompt was forgotten.</summary>
    internal static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(120);

    private static readonly string[] Pkexec = LinuxAdministratorAccess.Pkexec;
    private static readonly string[] Cat = ["/usr/bin/cat", "/bin/cat"];
    private static readonly string[] Dmidecode = ["/usr/sbin/dmidecode", "/usr/bin/dmidecode", "/sbin/dmidecode", "/bin/dmidecode"];
    private static readonly string[] Btrfs = ["/usr/bin/btrfs", "/usr/sbin/btrfs", "/bin/btrfs", "/sbin/btrfs"];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly LinuxAdministratorAccess _access;
    private readonly PkexecTools _tools;
    private readonly Func<string, bool> _exists;
    private readonly Func<string> _readMountInfo;
    private readonly Func<string, VfsCounters?> _statVfs;

    public LinuxPrivilegedOperations()
        : this(
            ElevatedRunner.RunAsync,
            PkexecChannel.Launch,
            File.Exists,
            PathTrust.Lstat,
            PromptTimeout,
            MountTable.ReadSystem,
            SystemSpaceStatVfs.Read,
            TimeProvider.System,
            AppContext.BaseDirectory,
            HelperSetup.EmbeddedScript)
    {
    }

    /// <param name="run">Runs pkexec for the fixed tools and the setup; tests hand in a fake.</param>
    /// <param name="launch">Starts <c>pkexec &lt;helper&gt; serve</c>; tests hand in a fake.</param>
    /// <param name="exists">Whether a program is installed at an absolute path (<c>pkexec</c>, <c>cat</c>, <c>dmidecode</c>, <c>btrfs</c>, <c>bash</c>).</param>
    /// <param name="lstat">Reads one path without following a link, for <see cref="HelperTrust"/>.</param>
    /// <param name="promptTimeout">How long a person gets to answer the password dialog.</param>
    /// <param name="readMountInfo">The text of <c>/proc/self/mountinfo</c>, to tell btrfs mounts from the others.</param>
    /// <param name="statVfs">The <c>statvfs</c> counters of a folder, for the space the file system reserves for root.</param>
    /// <param name="time">The clock the session's idle lock runs on.</param>
    /// <param name="appFolder">Where the app runs from; the setup looks for the helper and the policy next to it.</param>
    /// <param name="script">The installer text the setup runs as root; null when this build has none.</param>
    internal LinuxPrivilegedOperations(
        ElevatedRunnerFunc run,
        SessionLauncher launch,
        Func<string, bool> exists,
        Func<string, EntryInfo> lstat,
        TimeSpan promptTimeout,
        Func<string> readMountInfo,
        Func<string, VfsCounters?> statVfs,
        TimeProvider time,
        string appFolder,
        Func<string?> script)
    {
        var session = new ElevationSession(launch, time, promptTimeout);
        var setup = new HelperSetup(run, exists, lstat, appFolder, script, promptTimeout + TimeSpan.FromSeconds(60));
        _access = new LinuxAdministratorAccess(session, setup, exists, lstat);
        _tools = new PkexecTools(run, promptTimeout);
        _exists = exists;
        _readMountInfo = readMountInfo;
        _statVfs = statVfs;
    }

    public IAdministratorAccess Access => _access;

    public PrivilegedCapability Capability(PrivilegedOperation operation)
    {
        if (Find(Pkexec) is null)
        {
            return PrivilegedCapability.Missing(PrivilegedUnavailableReason.NoPkexec);
        }

        return operation switch
        {
            // Readable through the session or, failing that, through fixed system tools.
            PrivilegedOperation.FirmwareTables or PrivilegedOperation.SystemSpace => PrivilegedCapability.Ready,
            PrivilegedOperation.Folders or PrivilegedOperation.SystemCleanup => _access.HelperCapability(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };
    }

    /// <remarks>
    /// Through the helper, any failure is a failure. Through <c>cat</c>, a table that cannot be read even as root (an ordinary
    /// non-zero exit) is an empty one: there is nothing to parse and <see cref="ReadMemoryDevicesTextAsync"/> may know better.
    /// </remarks>
    public async Task<PrivilegedResult<byte[]>> ReadFirmwareTablesAsync(CancellationToken cancellationToken, bool promptIfNeeded = true)
    {
        if (Find(Pkexec) is not { } pkexec)
        {
            return PrivilegedResult<byte[]>.NotAvailable(PrivilegedUnavailableReason.NoPkexec);
        }

        if (_access.HelperCapability().Available)
        {
            PrivilegedResult<ServeResponse> answer = await _access.CallAsync(Request(HelperProtocol.ReadFirmwareTables), promptIfNeeded, cancellationToken);
            return Map(answer, ReadTable);
        }

        if (!promptIfNeeded)
        {
            return PrivilegedResult<byte[]>.NotAvailable(PrivilegedUnavailableReason.NotActive);
        }

        if (Find(Cat) is not { } cat)
        {
            return PrivilegedResult<byte[]>.Done([]);
        }

        ElevatedRun run = await _tools.RunToolAsync(pkexec, [cat, HelperProtocol.FirmwareTablePath], cancellationToken);
        return PkexecTools.Settle(run) ?? PrivilegedResult<byte[]>.Done([]);
    }

    public async Task<PrivilegedResult<string>> ReadMemoryDevicesTextAsync(CancellationToken cancellationToken)
    {
        if (Find(Pkexec) is not { } pkexec)
        {
            return PrivilegedResult<string>.NotAvailable(PrivilegedUnavailableReason.NoPkexec);
        }

        if (Find(Dmidecode) is not { } dmidecode)
        {
            return PrivilegedResult<string>.Failed("dmidecode is not installed.");
        }

        // The helper has no operation for this on purpose (it would be a way to run a system tool as root); it is always a one-off prompt.
        ElevatedRun run = await _tools.RunToolAsync(pkexec, [dmidecode, "-t", "17"], cancellationToken);
        PrivilegedResult<byte[]> settled = PkexecTools.Settle(run) ?? PrivilegedResult<byte[]>.Failed(PkexecTools.Detail(run));
        return settled.Outcome == PrivilegedOutcome.Done
            ? PrivilegedResult<string>.Done(Encoding.UTF8.GetString(settled.Value!))
            : settled.As<string>();
    }

    public async Task<PrivilegedResult<HiddenSpaceReport>> MeasureSystemSpaceAsync(string mountPoint, CancellationToken cancellationToken, bool promptIfNeeded = true)
    {
        if (Find(Pkexec) is not { } pkexec)
        {
            return PrivilegedResult<HiddenSpaceReport>.NotAvailable(PrivilegedUnavailableReason.NoPkexec);
        }

        if (!IsAbsolute(mountPoint))
        {
            return PrivilegedResult<HiddenSpaceReport>.Failed("The mount point is not an absolute path.");
        }

        if (_access.HelperCapability().Available)
        {
            PrivilegedResult<ServeResponse> answer = await _access.CallAsync(Request(HelperProtocol.MeasureSystemSpace, mountPoint), promptIfNeeded, cancellationToken);
            return Map(answer, ReadSystemSpace);
        }

        return promptIfNeeded
            ? await MeasureWithToolsAsync(pkexec, mountPoint, cancellationToken)
            : PrivilegedResult<HiddenSpaceReport>.NotAvailable(PrivilegedUnavailableReason.NotActive);
    }

    public async Task<PrivilegedResult<IReadOnlyList<MeasuredFolder>>> MeasureFoldersAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken, bool promptIfNeeded = true)
    {
        string[] wanted = [.. paths.Where(IsAbsolute).Distinct(StringComparer.Ordinal)];
        var measured = new List<MeasuredFolder>(wanted.Length);
        if (wanted.Length == 0)
        {
            return PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done(measured); // Nothing to ask a password for.
        }

        // One request holds at most MaxFolders; the session is already running for every one after the first, so only the first can ask.
        for (int start = 0; start < wanted.Length; start += HelperProtocol.MaxFolders)
        {
            string[] chunk = wanted[start..Math.Min(wanted.Length, start + HelperProtocol.MaxFolders)];
            PrivilegedResult<ServeResponse> answer = await _access.CallAsync(
                new ServeRequest(HelperProtocol.Version, 0, HelperProtocol.MeasureFolders, Paths: chunk), promptIfNeeded, cancellationToken);
            PrivilegedResult<FoldersResponse> folders = Map(answer, static response => Present(response.Folders));
            if (folders.Outcome != PrivilegedOutcome.Done)
            {
                return folders.As<IReadOnlyList<MeasuredFolder>>();
            }

            foreach (FolderSize folder in folders.Value!.Folders ?? [])
            {
                measured.Add(new MeasuredFolder(folder.Path, folder.SizeOnDisk, folder.LogicalSize, folder.Files, folder.Complete));
            }
        }

        return PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done(measured);
    }

    public async Task<PrivilegedResult<CleanupResult>> CleanSystemAsync(CleanupEstimate estimate, CancellationToken cancellationToken)
    {
        string ruleId = estimate.Rule.Id;
        if (!estimate.Rule.RequiresAdmin || SystemCleanupRules.FoldersOf(ruleId) is not { } roots)
        {
            return PrivilegedResult<CleanupResult>.Failed($"'{ruleId}' is not a rule the helper cleans.");
        }

        (List<CleanFile> files, _, long notSent) = Prepare(estimate.Files ?? [], roots);
        if (files.Count == 0)
        {
            return PrivilegedResult<CleanupResult>.Done(new CleanupResult(estimate.Rule, 0, 0, notSent)); // Nothing to ask a password for.
        }

        PrivilegedResult<ServeResponse> answer = await _access.CallAsync(
            new ServeRequest(HelperProtocol.Version, 0, HelperProtocol.CleanSystem, ruleId, Files: files), allowPrompt: true, cancellationToken);
        PrivilegedResult<CleanResponse> cleaned = Map(answer, static response => Present(response.Clean));
        if (cleaned.Outcome != PrivilegedOutcome.Done)
        {
            return cleaned.As<CleanupResult>();
        }

        CleanResponse done = cleaned.Value!;
        return PrivilegedResult<CleanupResult>.Done(new CleanupResult(estimate.Rule, done.FreedBytes, done.Deleted, done.Skipped + notSent));
    }

    private static ServeRequest Request(string op, string? argument = null) => new(HelperProtocol.Version, 0, op, argument);

    /// <summary>
    /// The answer's payload when the helper answered with success; otherwise the outcome that says why. An answer that is
    /// <see cref="HelperExit.Usage"/> means the installed helper does not understand what this app asks: an older one.
    /// </summary>
    private static PrivilegedResult<T> Map<T>(PrivilegedResult<ServeResponse> answer, Func<ServeResponse, PrivilegedResult<T>> payload)
    {
        if (answer.Outcome != PrivilegedOutcome.Done)
        {
            return answer.As<T>();
        }

        ServeResponse response = answer.Value!;
        if (response.Exit == HelperExit.Usage)
        {
            return PrivilegedResult<T>.NotAvailable(PrivilegedUnavailableReason.HelperOutdated);
        }

        if (response.Exit != HelperExit.Ok)
        {
            return PrivilegedResult<T>.Failed(response.Error ?? $"The helper stopped with code {response.Exit}.");
        }

        return payload(response);
    }

    private static PrivilegedResult<T> Present<T>(T? value)
        where T : class =>
        value is null ? PrivilegedResult<T>.Failed("The helper's answer was empty.") : PrivilegedResult<T>.Done(value);

    /// <summary>
    /// The files of the preview the helper may be asked about, as its request, and how many were left out. A file goes
    /// only when it is directly below one of the rule's folders under a name that is plain text, is not one the helper
    /// always keeps, and fits in the request; the helper checks all of it again.
    /// </summary>
    internal static (List<CleanFile> Files, byte[] Request, long NotSent) Prepare(IReadOnlyList<CleanupFile> preview, IReadOnlyList<string> roots)
    {
        var files = new List<CleanFile>();
        foreach (CleanupFile file in preview)
        {
            if (files.Count < HelperProtocol.MaxCleanFiles && ToCleanFile(file, roots) is { } entry)
            {
                files.Add(entry);
            }
        }

        byte[] request = Serialize(files);
        while (request.Length > HelperProtocol.MaxRequestBytes)
        {
            // Too many long names: send what fits (with a margin, since the size is not exactly proportional).
            int keep = (int)((long)files.Count * HelperProtocol.MaxRequestBytes / request.Length * 9 / 10);
            files.RemoveRange(keep, files.Count - keep);
            request = Serialize(files);
        }

        return (files, request, preview.Count - files.Count);
    }

    private static byte[] Serialize(List<CleanFile> files) =>
        JsonSerializer.SerializeToUtf8Bytes(new CleanRequest(HelperProtocol.Version, files), HelperJson.Default.CleanRequest);

    private static CleanFile? ToCleanFile(CleanupFile file, IReadOnlyList<string> roots)
    {
        string path = file.Path;
        foreach (string root in roots)
        {
            if (!path.StartsWith(root, StringComparison.Ordinal) || path.Length <= root.Length + 1 || path[root.Length] != '/')
            {
                continue;
            }

            string relative = path[(root.Length + 1)..];
            if (!IsPlainRelative(relative) || SystemCleanupRules.IsKept(root, relative))
            {
                return null;
            }

            return new CleanFile(root, relative, file.Size, file.ChangedUtc.Ticks);
        }

        return null;
    }

    /// <summary>
    /// A path that survives the trip through JSON and back to the same bytes, and names something below the folder:
    /// no empty, <c>.</c> or <c>..</c> part, no NUL, and nothing .NET had to replace when it decoded the file name
    /// (a name that is not UTF-8 shows up as U+FFFD or as a lone surrogate).
    /// </summary>
    private static bool IsPlainRelative(string relative)
    {
        if (relative.Contains('\0') || relative.Contains('\uFFFD'))
        {
            return false;
        }

        try
        {
            StrictUtf8.GetByteCount(relative);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        foreach (string part in relative.Split('/'))
        {
            if (part is "" or "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The same measurement the helper makes, assembled from <c>btrfs</c> run through <c>pkexec</c>: what only a file system's
    /// administrator can see on btrfs. Any other file system has nothing to ask for, so no prompt is shown for it.
    /// </summary>
    private async Task<PrivilegedResult<HiddenSpaceReport>> MeasureWithToolsAsync(string pkexec, string mountPoint, CancellationToken cancellationToken)
    {
        string root = mountPoint.Length > 1 ? mountPoint.TrimEnd('/') : mountPoint;
        long? reserved = _statVfs(root) is { } counters ? SystemSpaceParsers.ReservedBytes(counters) : null;
        MountEntry? mount = SystemSpaceParsers.FindMount(MountTable.Parse(_readMountInfo()), root);

        if (mount is not { FileSystemType: "btrfs" } || Find(Btrfs) is not { } btrfs)
        {
            return PrivilegedResult<HiddenSpaceReport>.Done(new HiddenSpaceReport(null, reserved));
        }

        string?[] texts = new string?[3];
        string[][] commands =
        [
            [btrfs, "filesystem", "usage", "-b", root],
            [btrfs, "qgroup", "show", "-pcre", "--raw", root],
            [btrfs, "subvolume", "list", "-s", root],
        ];

        for (int i = 0; i < commands.Length; i++)
        {
            ElevatedRun run = await _tools.RunToolAsync(pkexec, commands[i], cancellationToken);
            if (PkexecTools.Settle(run) is not { } settled)
            {
                continue; // An ordinary failure (quotas off, no snapshots): that part stays unknown.
            }

            if (settled.Outcome != PrivilegedOutcome.Done)
            {
                return settled.As<HiddenSpaceReport>();
            }

            texts[i] = Encoding.UTF8.GetString(settled.Value!);
        }

        SystemSpaceResponse space = BtrfsSpace.Build("btrfs", reserved, texts[0], texts[2], texts[1]);
        return PrivilegedResult<HiddenSpaceReport>.Done(new HiddenSpaceReport(space.SnapshotBytes, space.MetadataBytes));
    }

    private static PrivilegedResult<byte[]> ReadTable(ServeResponse response)
    {
        if (response.Firmware is not { } table)
        {
            return PrivilegedResult<byte[]>.Failed("The helper's answer was empty.");
        }

        try
        {
            return PrivilegedResult<byte[]>.Done(Convert.FromBase64String(table.Table ?? ""));
        }
        catch (FormatException)
        {
            return PrivilegedResult<byte[]>.Failed("The helper's firmware table was not base64.");
        }
    }

    private static PrivilegedResult<HiddenSpaceReport> ReadSystemSpace(ServeResponse response) =>
        response.Space is { } space
            ? PrivilegedResult<HiddenSpaceReport>.Done(new HiddenSpaceReport(space.SnapshotBytes, space.MetadataBytes))
            : PrivilegedResult<HiddenSpaceReport>.Failed("The helper's answer was empty.");

    private static bool IsAbsolute(string? path) => !string.IsNullOrEmpty(path) && path[0] == '/' && !path.Contains('\0');

    private string? Find(string[] candidates) => candidates.FirstOrDefault(_exists);
}
