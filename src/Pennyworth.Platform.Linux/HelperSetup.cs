using System.Security.Cryptography;
using Pennyworth.Core.Privilege;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The one-time, explicitly approved installation of the helper and its polkit policy from the files this copy of the app carries
/// (the tarball's <c>libexec/</c> and <c>share/polkit-1/</c>). It is <c>tools/package/linux/install-helper.sh</c> (compiled into the app
/// as a resource) run through <c>pkexec bash -c &lt;script&gt;</c>: root runs a fixed text that ships with the app, never a script file
/// from a folder the user can write, and that script copies the two files to a root-owned place, checks the copy against the SHA-256
/// the person was shown, refuses symbolic links and anything that is not the expected kind of file, and only then moves them into place.
/// It starts nothing.
/// </summary>
/// <remarks>
/// What this cannot do, and says so: the files come from a folder the user can write, so something that already runs as the user
/// could have replaced them before the plan was read, and the hash the person is shown would then be the replacement's. The person is
/// explicitly authorising an install of the app's own files; comparing the hashes with the release's published ones is the check
/// that covers it.
/// </remarks>
internal sealed class HelperSetup
{
    /// <summary>The longest helper the plan accepts (the real one is a few megabytes).</summary>
    internal const long MaxHelperBytes = 64L * 1024 * 1024;

    /// <summary>The longest policy the plan accepts (the real one is a few kilobytes).</summary>
    internal const long MaxPolicyBytes = 256L * 1024;

    internal const string ScriptResource = "install-helper.sh";

    private const string PolicyPath = "/usr/share/polkit-1/actions/app.pennyworth.policy";

    private static readonly string[] Bash = ["/usr/bin/bash", "/bin/bash"];

    private readonly ElevatedRunnerFunc _run;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, EntryInfo> _lstat;
    private readonly string _appFolder;
    private readonly Func<string?> _script;
    private readonly TimeSpan _timeout;

    /// <param name="run">Runs pkexec; tests hand in a fake.</param>
    /// <param name="exists">Whether a program exists at an absolute path.</param>
    /// <param name="lstat">Reads one path without following a link, for <see cref="HelperTrust"/>.</param>
    /// <param name="appFolder">The folder the app runs from (<c>lib/pennyworth</c> of the tarball); the helper and the policy are looked for relative to it.</param>
    /// <param name="script">The text of the installer, null when it is not part of this build.</param>
    /// <param name="timeout">How long the prompt and the install may take.</param>
    internal HelperSetup(ElevatedRunnerFunc run, Func<string, bool> exists, Func<string, EntryInfo> lstat, string appFolder, Func<string?> script, TimeSpan timeout)
    {
        _run = run;
        _exists = exists;
        _lstat = lstat;
        _appFolder = appFolder;
        _script = script;
        _timeout = timeout;
    }

    /// <summary>The installer as the build embedded it.</summary>
    internal static string? EmbeddedScript()
    {
        using Stream? stream = typeof(HelperSetup).Assembly.GetManifestResourceStream(ScriptResource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Where this copy of the app keeps the helper and the policy: the sibling folders of <c>lib/pennyworth</c>.</summary>
    internal (string Helper, string Policy) BundledFiles()
    {
        string prefix = Path.GetFullPath(Path.Combine(_appFolder, "..", ".."));
        return (
            Path.Combine(prefix, "libexec", "pennyworth", "pennyworth-helper"),
            Path.Combine(prefix, "share", "polkit-1", "actions", "app.pennyworth.policy"));
    }

    /// <summary>
    /// Reads the two files the setup would install and hashes them. <see cref="PrivilegedUnavailableReason.SetupNotBundled"/> when
    /// this copy has no helper; a failure (with the reason in <see cref="PrivilegedResult{T}.Detail"/>) when a file is not what it must be.
    /// </summary>
    internal async Task<PrivilegedResult<AdministratorSetupPlan>> PrepareAsync(CancellationToken cancellationToken)
    {
        (string helper, string policy) = BundledFiles();
        if (_script() is null || !File.Exists(helper) || !File.Exists(policy))
        {
            return PrivilegedResult<AdministratorSetupPlan>.NotAvailable(PrivilegedUnavailableReason.SetupNotBundled);
        }

        try
        {
            if (!IsPlainFile(helper) || !IsPlainFile(policy))
            {
                return PrivilegedResult<AdministratorSetupPlan>.Failed("A file to install is a link or not a regular file.");
            }

            (string helperHash, long helperBytes, bool isElf) = await HashAsync(helper, MaxHelperBytes, cancellationToken).ConfigureAwait(false);
            if (!isElf)
            {
                return PrivilegedResult<AdministratorSetupPlan>.Failed("The helper is not an ELF program.");
            }

            (string policyHash, _, _) = await HashAsync(policy, MaxPolicyBytes, cancellationToken).ConfigureAwait(false);
            return PrivilegedResult<AdministratorSetupPlan>.Done(new AdministratorSetupPlan(
                helper, helperHash, helperBytes, policy, policyHash, HelperProtocol.InstalledPath, PolicyPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return PrivilegedResult<AdministratorSetupPlan>.Failed("A file to install could not be read: " + ex.Message);
        }
    }

    /// <summary>Runs the installer for <paramref name="plan"/> through <c>pkexec</c>; Done only if the helper then passes <see cref="HelperTrust"/>.</summary>
    internal async Task<PrivilegedResult<bool>> InstallAsync(string pkexec, AdministratorSetupPlan plan, CancellationToken cancellationToken)
    {
        string? script = _script();
        string? bash = Bash.FirstOrDefault(_exists);
        if (script is null)
        {
            return PrivilegedResult<bool>.NotAvailable(PrivilegedUnavailableReason.SetupNotBundled);
        }

        if (bash is null)
        {
            return PrivilegedResult<bool>.Failed("bash is not installed.");
        }

        if (plan.HelperDestination != HelperProtocol.InstalledPath)
        {
            return PrivilegedResult<bool>.Failed("The plan does not install to the helper's fixed path.");
        }

        string[] program =
        [
            bash, "-c", script, "pennyworth-install",
            "--helper", plan.HelperSource, "--helper-sha256", plan.HelperSha256,
            "--policy", plan.PolicySource, "--policy-sha256", plan.PolicySha256,
        ];
        ElevatedRun run = await _run(pkexec, program, null, ElevatedRunner.MaxOutput, _timeout, cancellationToken).ConfigureAwait(false);

        if (PkexecTools.Settle(run) is { } settled)
        {
            return settled.Outcome == PrivilegedOutcome.Done ? Verified() : settled.As<bool>();
        }

        return PrivilegedResult<bool>.Failed(PkexecTools.Detail(run));
    }

    private PrivilegedResult<bool> Verified() => HelperTrust.Evaluate(_lstat).Available
        ? PrivilegedResult<bool>.Done(true)
        : PrivilegedResult<bool>.Failed("The helper was installed but is not trusted at " + HelperProtocol.InstalledPath + ".");

    /// <summary>A regular file reached without a link in its last component (the installer checks the whole path again as root).</summary>
    private static bool IsPlainFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget is null && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static async Task<(string Hash, long Bytes, bool IsElf)> HashAsync(string path, long limit, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > limit)
        {
            throw new InvalidDataException("the file is larger than " + limit + " bytes");
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        bool isElf = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (total == 0)
            {
                isElf = read >= 4 && buffer[0] == 0x7f && buffer[1] == (byte)'E' && buffer[2] == (byte)'L' && buffer[3] == (byte)'F';
            }

            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("the file is larger than " + limit + " bytes");
            }

            sha.AppendData(buffer, 0, read);
        }

        return (Convert.ToHexStringLower(sha.GetHashAndReset()), total, isElf);
    }
}
