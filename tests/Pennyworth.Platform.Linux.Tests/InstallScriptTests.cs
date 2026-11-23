using System.Diagnostics;
using System.Security.Cryptography;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>
/// The installer the app runs as root through <c>pkexec bash -c</c>, run here in its own staging mode (a temporary DESTDIR, as the
/// calling user): it must install what was approved and nothing else, and refuse a copy that does not match the SHA-256 the person was
/// shown. The text the app runs is the very file the tarball ships.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class InstallScriptTests : IDisposable
{
    private static readonly string ScriptPath = Path.Combine(RepoRoot(), "tools", "package", "linux", "install-helper.sh");
    private static readonly string PolicyPath = Path.Combine(RepoRoot(), "tools", "package", "linux", "app.pennyworth.policy");

    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pennyworth.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Pennyworth.slnx was not found above " + AppContext.BaseDirectory);
    }

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record Result(int Exit, string Out, string Err);

    /// <summary>Runs bash with <paramref name="arguments"/> in staging mode (DESTDIR below the temp tree, no root needed).</summary>
    private Result Bash(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = { ["DESTDIR"] = _tree.PathOf("dest"), ["PENNYWORTH_INSTALL_TEST"] = "1", ["LC_ALL"] = "C" },
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new Result(process.ExitCode, output, error.GetAwaiter().GetResult());
    }

    /// <summary>The installer the way the app runs it: its text as <c>bash -c</c>, with a fixed <c>$0</c>.</summary>
    private Result AsTheAppRunsIt(string helper, string policy, string? helperSha, string? policySha)
    {
        List<string> arguments = ["-c", File.ReadAllText(ScriptPath), "pennyworth-install", "--helper", helper, "--policy", policy];
        if (helperSha is not null)
        {
            arguments.AddRange(["--helper-sha256", helperSha]);
        }

        if (policySha is not null)
        {
            arguments.AddRange(["--policy-sha256", policySha]);
        }

        return Bash(arguments);
    }

    private string Helper()
    {
        string path = _tree.PathOf("src/pennyworth-helper");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy("/usr/bin/true", path); // any ELF executable will do
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string Destination(string relative) => _tree.PathOf("dest" + relative);

    [LinuxFact]
    public void The_app_embeds_exactly_the_script_the_tarball_ships()
    {
        Assert.Equal(File.ReadAllText(ScriptPath), HelperSetup.EmbeddedScript());
    }

    [LinuxFact]
    public void The_approved_files_are_installed_when_their_hashes_match_and_the_script_works_as_bash_dash_c()
    {
        string helper = Helper();

        Result result = AsTheAppRunsIt(helper, PolicyPath, Sha(helper), Sha(PolicyPath));

        Assert.True(result.Exit == 0, result.Err);
        Assert.Equal(Sha(helper), Sha(Destination(HelperProtocol.InstalledPath)));
        Assert.Equal(Sha(PolicyPath), Sha(Destination("/usr/share/polkit-1/actions/app.pennyworth.policy")));
        Assert.Contains("installed /usr/libexec/pennyworth/pennyworth-helper", result.Out);
        Assert.DoesNotContain("sudo $0", result.Out);
    }

    [LinuxFact]
    public void A_copy_that_is_not_the_approved_file_is_refused_and_nothing_stays_behind()
    {
        string helper = Helper();
        string wrong = new('0', 64);

        Result result = AsTheAppRunsIt(helper, PolicyPath, wrong, Sha(PolicyPath));

        Assert.Equal(1, result.Exit);
        Assert.Contains("is not the file that was approved", result.Err);
        Assert.False(File.Exists(Destination(HelperProtocol.InstalledPath)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination(HelperProtocol.InstalledPath))!));
        Assert.False(File.Exists(Destination("/usr/share/polkit-1/actions/app.pennyworth.policy")));
    }

    [LinuxFact]
    public void A_policy_that_does_not_match_its_hash_refuses_the_whole_install_helper_included()
    {
        string helper = Helper();

        Result result = AsTheAppRunsIt(helper, PolicyPath, Sha(helper), new string('f', 64));

        Assert.Equal(1, result.Exit);
        Assert.Contains("is not the file that was approved", result.Err);
        Assert.False(File.Exists(Destination(HelperProtocol.InstalledPath))); // both or neither
        Assert.False(File.Exists(Destination("/usr/share/polkit-1/actions/app.pennyworth.policy")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination(HelperProtocol.InstalledPath))!));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination("/usr/share/polkit-1/actions/app.pennyworth.policy"))!));
    }

    [LinuxFact]
    public void A_source_that_changed_after_it_was_approved_is_refused()
    {
        string helper = Helper();
        string approved = Sha(helper);
        File.AppendAllText(helper, "tampered after the prompt was shown");

        Result result = AsTheAppRunsIt(helper, PolicyPath, approved, Sha(PolicyPath));

        Assert.Equal(1, result.Exit);
        Assert.False(File.Exists(Destination(HelperProtocol.InstalledPath)));
    }

    [LinuxFact]
    public void A_link_as_a_source_is_refused_whatever_it_points_to()
    {
        string real = Helper();
        string link = _tree.PathOf("src/linked-helper");
        File.CreateSymbolicLink(link, real);
        string policyLink = _tree.PathOf("src/linked.policy");
        File.CreateSymbolicLink(policyLink, PolicyPath);

        Result helperLinked = AsTheAppRunsIt(link, PolicyPath, Sha(real), Sha(PolicyPath));
        Result policyLinked = AsTheAppRunsIt(real, policyLink, Sha(real), Sha(PolicyPath));

        Assert.Equal(1, helperLinked.Exit);
        Assert.Equal(1, policyLinked.Exit);
        Assert.False(File.Exists(Destination(HelperProtocol.InstalledPath)));
    }

    [LinuxFact]
    public void A_file_that_is_not_an_elf_program_is_refused()
    {
        string script = _tree.PathOf("src/not-elf");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Result result = AsTheAppRunsIt(script, PolicyPath, Sha(script), Sha(PolicyPath));

        Assert.Equal(1, result.Exit);
        Assert.Contains("not an ELF", result.Err);
    }

    [LinuxFact]
    public void A_policy_that_names_another_helper_is_refused()
    {
        string helper = Helper();
        string policy = _tree.PathOf("src/other.policy");
        File.WriteAllText(policy, File.ReadAllText(PolicyPath).Replace(HelperProtocol.InstalledPath, "/tmp/evil"));

        Result result = AsTheAppRunsIt(helper, policy, Sha(helper), Sha(policy));

        Assert.Equal(1, result.Exit);
        Assert.False(File.Exists(Destination(HelperProtocol.InstalledPath)));
    }

    [LinuxFact]
    public void Without_hashes_the_script_still_installs_for_a_person_who_runs_it_by_hand()
    {
        string helper = Helper();

        Result result = Bash([ScriptPath, "--helper", helper, "--policy", PolicyPath]);

        Assert.True(result.Exit == 0, result.Err);
        Assert.True(File.Exists(Destination(HelperProtocol.InstalledPath)));
        Assert.Contains("sudo rm /usr/libexec/pennyworth/pennyworth-helper", result.Out);
    }

    [LinuxFact]
    public void Running_it_again_with_the_same_files_says_unchanged_and_still_checks_the_hash()
    {
        string helper = Helper();
        AsTheAppRunsIt(helper, PolicyPath, Sha(helper), Sha(PolicyPath));

        Result again = AsTheAppRunsIt(helper, PolicyPath, Sha(helper), Sha(PolicyPath));
        Result wrong = AsTheAppRunsIt(helper, PolicyPath, new string('1', 64), Sha(PolicyPath));

        Assert.True(again.Exit == 0, again.Err);
        Assert.Contains("unchanged /usr/libexec/pennyworth/pennyworth-helper", again.Out);
        Assert.Equal(1, wrong.Exit);
    }

    [LinuxFact]
    public void Without_root_and_without_the_staging_switches_it_refuses_to_touch_the_system()
    {
        var start = new ProcessStartInfo("/usr/bin/bash") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add(ScriptPath);
        start.ArgumentList.Add("--helper");
        start.ArgumentList.Add(Helper());
        start.Environment.Remove("DESTDIR");
        start.Environment.Remove("PENNYWORTH_INSTALL_TEST");

        using Process process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("must run as root", error);
        Assert.False(File.Exists(HelperProtocol.InstalledPath) && new FileInfo(HelperProtocol.InstalledPath).LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [LinuxFact]
    public void The_policy_and_the_script_agree_on_the_helper_path()
    {
        string script = File.ReadAllText(ScriptPath);

        Assert.Contains("helper_dest=\"$helper_dir/pennyworth-helper\"", script, StringComparison.Ordinal);
        Assert.Contains("helper_dir=/usr/libexec/pennyworth", script, StringComparison.Ordinal);
        Assert.Contains(HelperProtocol.InstalledPath, File.ReadAllText(PolicyPath), StringComparison.Ordinal);
    }
}
