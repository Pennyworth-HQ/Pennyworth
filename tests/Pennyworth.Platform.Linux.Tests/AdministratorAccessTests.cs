using System.Security.Cryptography;
using System.Text;
using Pennyworth.Core.Privilege;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>
/// The Off / Active / Needs setup / Unavailable state machine of <see cref="IAdministratorAccess"/> and the one-time setup flow that
/// takes a machine from "needs setup" to "off, ready for the one prompt". A fake plays pkexec and a temporary folder plays the app's
/// own folder; nothing is installed anywhere and no root rights or password are involved.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class AdministratorAccessTests : IDisposable
{
    private const string Pkexec = Privileged.Pkexec;
    private const string Script = "#!/bin/bash\necho the installer\n";

    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string AppFolder => _tree.PathOf("prefix/lib/pennyworth");

    private string BundledHelper => _tree.PathOf("prefix/libexec/pennyworth/pennyworth-helper");

    private string BundledPolicy => _tree.PathOf("prefix/share/polkit-1/actions/app.pennyworth.policy");

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private void Bundle(byte[]? helper = null, byte[]? policy = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BundledHelper)!);
        Directory.CreateDirectory(Path.GetDirectoryName(BundledPolicy)!);
        File.WriteAllBytes(BundledHelper, helper ?? [0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3, 4, 5]);
        File.WriteAllBytes(BundledPolicy, policy ?? "<policyconfig/>"u8.ToArray());
    }

    private LinuxPrivilegedOperations Ops(FakeRunner runner, Func<string, EntryInfo> lstat, FakeLauncher? launcher = null, string[]? installed = null, Func<string?>? script = null) =>
        Ops(runner.Run, lstat, launcher, installed, script);

    private LinuxPrivilegedOperations Ops(ElevatedRunnerFunc run, Func<string, EntryInfo> lstat, FakeLauncher? launcher = null, string[]? installed = null, Func<string?>? script = null) =>
        new(
            run,
            (launcher ?? new FakeLauncher()).Launch,
            (installed ?? [Pkexec, "/usr/bin/cat", "/usr/bin/bash"]).Contains,
            lstat,
            TimeSpan.FromSeconds(5),
            () => "",
            _ => null,
            new ManualTime(),
            AppFolder,
            script ?? (() => Script));

    // ---- the state ----

    [LinuxFact]
    public void A_trusted_helper_means_off_ready_for_one_prompt()
    {
        IAdministratorAccess access = Ops(new FakeRunner(), Privileged.TrustedLstat).Access;

        Assert.Equal(AdministratorAccessState.Off, access.State);
        Assert.Null(access.Reason);
        Assert.Equal(TimeSpan.FromMinutes(15), access.IdleTimeout);
    }

    [LinuxTheory]
    [InlineData("missing", PrivilegedUnavailableReason.HelperNotInstalled)]
    [InlineData("group-writable", PrivilegedUnavailableReason.HelperNotTrusted)]
    [InlineData("user-owned", PrivilegedUnavailableReason.HelperNotTrusted)]
    public void No_trusted_helper_means_needs_setup_with_the_reason(string lstat, PrivilegedUnavailableReason reason)
    {
        Func<string, EntryInfo> fs = lstat switch
        {
            "missing" => Privileged.MissingLstat,
            "group-writable" => Privileged.GroupWritableLstat,
            _ => Privileged.UserOwnedLstat,
        };

        IAdministratorAccess access = Ops(new FakeRunner(), fs).Access;

        Assert.Equal((AdministratorAccessState.NeedsSetup, reason), (access.State, access.Reason));
    }

    [LinuxFact]
    public void Without_pkexec_it_is_unavailable_and_nothing_offers_a_setup()
    {
        IAdministratorAccess access = Ops(new FakeRunner(), Privileged.MissingLstat, installed: ["/usr/bin/cat"]).Access;

        Assert.Equal((AdministratorAccessState.Unavailable, PrivilegedUnavailableReason.NoPkexec), (access.State, access.Reason));
    }

    [LinuxFact]
    public async Task Turning_on_asks_once_becomes_active_and_turning_on_again_asks_nothing()
    {
        var launcher = FakeLauncher.Serving(ServeFakes.Seams());
        IAdministratorAccess access = Ops(new FakeRunner(), Privileged.TrustedLstat, launcher).Access;
        var states = new List<AdministratorAccessState>();
        access.StateChanged += () => states.Add(access.State);

        PrivilegedResult<bool> first = await access.TurnOnAsync(Token);
        PrivilegedResult<bool> second = await access.TurnOnAsync(Token);

        Assert.Equal((PrivilegedOutcome.Done, true), (first.Outcome, first.Value));
        Assert.Equal(PrivilegedOutcome.Done, second.Outcome);
        Assert.Equal(1, launcher.Starts);
        Assert.Equal(AdministratorAccessState.Active, access.State);
        Assert.Equal([AdministratorAccessState.Active], states);
    }

    [LinuxFact]
    public async Task Locking_returns_to_off_and_says_so()
    {
        var launcher = FakeLauncher.Serving(ServeFakes.Seams());
        IAdministratorAccess access = Ops(new FakeRunner(), Privileged.TrustedLstat, launcher).Access;
        await access.TurnOnAsync(Token);
        var states = new List<AdministratorAccessState>();
        access.StateChanged += () => states.Add(access.State);

        access.Lock();

        Assert.Equal(AdministratorAccessState.Off, access.State);
        Assert.Equal([AdministratorAccessState.Off], states);
    }

    [LinuxFact]
    public async Task A_dismissed_prompt_leaves_it_off_and_cancelled()
    {
        var launcher = new FakeLauncher(() => FakeChannel.Exits(126));
        IAdministratorAccess access = Ops(new FakeRunner(), Privileged.TrustedLstat, launcher).Access;

        PrivilegedResult<bool> result = await access.TurnOnAsync(Token);

        Assert.Equal(PrivilegedOutcome.Cancelled, result.Outcome);
        Assert.Equal(AdministratorAccessState.Off, access.State);
        Assert.Equal(1, launcher.Starts);
    }

    [LinuxFact]
    public async Task Turning_on_without_a_helper_does_not_prompt_and_names_why()
    {
        var launcher = new FakeLauncher();
        var runner = new FakeRunner();
        IAdministratorAccess access = Ops(runner, Privileged.MissingLstat, launcher).Access;

        PrivilegedResult<bool> result = await access.TurnOnAsync(Token);

        Assert.Equal((PrivilegedOutcome.NotAvailable, PrivilegedUnavailableReason.HelperNotInstalled), (result.Outcome, result.Reason));
        Assert.Equal(0, launcher.Starts);
        Assert.Empty(runner.Calls);
    }

    [LinuxFact]
    public async Task A_helper_found_outdated_when_it_starts_makes_it_needs_setup_with_that_reason_until_setup_replaces_it()
    {
        var launcher = new FakeLauncher(() => FakeChannel.Exits(2, "pennyworth-helper: unknown subcommand"));
        var runner = new FakeRunner(Privileged.Exit(0));
        Bundle();
        IAdministratorAccess access = Ops(runner, Privileged.TrustedLstat, launcher).Access;
        int changes = 0;
        access.StateChanged += () => changes++;

        PrivilegedResult<bool> result = await access.TurnOnAsync(Token);

        Assert.Equal(PrivilegedUnavailableReason.HelperOutdated, result.Reason);
        Assert.Equal((AdministratorAccessState.NeedsSetup, PrivilegedUnavailableReason.HelperOutdated), (access.State, access.Reason));
        Assert.Equal(1, changes);

        PrivilegedResult<AdministratorSetupPlan> plan = await access.PrepareSetupAsync(Token);
        PrivilegedResult<bool> installed = await access.SetupAsync(plan.Value!, Token);

        Assert.Equal(PrivilegedOutcome.Done, installed.Outcome);
        Assert.Equal(AdministratorAccessState.Off, access.State);
    }

    // ---- what the setup would install ----

    [LinuxFact]
    public async Task The_plan_names_both_files_their_sizes_hashes_and_destinations_without_running_anything()
    {
        byte[] helper = [0x7f, (byte)'E', (byte)'L', (byte)'F', 9, 9, 9];
        byte[] policy = Encoding.UTF8.GetBytes("<policyconfig>x</policyconfig>");
        Bundle(helper, policy);
        var runner = new FakeRunner();

        PrivilegedResult<AdministratorSetupPlan> result = await Ops(runner, Privileged.MissingLstat).Access.PrepareSetupAsync(Token);

        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        AdministratorSetupPlan plan = result.Value!;
        Assert.Equal(new AdministratorSetupPlan(
            BundledHelper, Sha(helper), helper.Length, BundledPolicy, Sha(policy),
            "/usr/libexec/pennyworth/pennyworth-helper", "/usr/share/polkit-1/actions/app.pennyworth.policy"), plan);
        Assert.Empty(runner.Calls);
    }

    [LinuxFact]
    public async Task A_copy_without_the_helper_or_the_installer_cannot_offer_a_setup()
    {
        PrivilegedResult<AdministratorSetupPlan> noFiles = await Ops(new FakeRunner(), Privileged.MissingLstat).Access.PrepareSetupAsync(Token);
        Bundle();
        PrivilegedResult<AdministratorSetupPlan> noScript = await Ops(new FakeRunner(), Privileged.MissingLstat, script: () => null).Access.PrepareSetupAsync(Token);

        Assert.Equal(PrivilegedUnavailableReason.SetupNotBundled, noFiles.Reason);
        Assert.Equal(PrivilegedUnavailableReason.SetupNotBundled, noScript.Reason);
    }

    [LinuxFact]
    public async Task Without_pkexec_there_is_no_plan()
    {
        Bundle();

        PrivilegedResult<AdministratorSetupPlan> result = await Ops(new FakeRunner(), Privileged.MissingLstat, installed: ["/usr/bin/bash"]).Access.PrepareSetupAsync(Token);

        Assert.Equal(PrivilegedUnavailableReason.NoPkexec, result.Reason);
    }

    [LinuxFact]
    public async Task A_link_or_a_program_that_is_not_elf_is_refused_with_a_reason()
    {
        byte[] elf = [0x7f, (byte)'E', (byte)'L', (byte)'F'];
        Bundle();
        File.Delete(BundledHelper);
        File.CreateSymbolicLink(BundledHelper, "/usr/bin/true");
        PrivilegedResult<AdministratorSetupPlan> linked = await Ops(new FakeRunner(), Privileged.MissingLstat).Access.PrepareSetupAsync(Token);

        File.Delete(BundledHelper); // removes the link, not its target
        File.WriteAllBytes(BundledHelper, "#!/bin/sh\n"u8.ToArray());
        PrivilegedResult<AdministratorSetupPlan> script = await Ops(new FakeRunner(), Privileged.MissingLstat).Access.PrepareSetupAsync(Token);

        File.WriteAllBytes(BundledHelper, elf);
        File.Delete(BundledPolicy);
        File.CreateSymbolicLink(BundledPolicy, "/etc/passwd");
        PrivilegedResult<AdministratorSetupPlan> linkedPolicy = await Ops(new FakeRunner(), Privileged.MissingLstat).Access.PrepareSetupAsync(Token);

        Assert.Equal(PrivilegedOutcome.Failed, linked.Outcome);
        Assert.Equal(PrivilegedOutcome.Failed, script.Outcome);
        Assert.Equal(PrivilegedOutcome.Failed, linkedPolicy.Outcome);
        Assert.True(File.Exists("/etc/passwd"));
    }

    [LinuxFact]
    public async Task An_oversized_helper_is_refused()
    {
        Bundle();
        using (var stream = File.Open(BundledHelper, FileMode.Open))
        {
            stream.SetLength(HelperSetup.MaxHelperBytes + 1);
        }

        PrivilegedResult<AdministratorSetupPlan> result = await Ops(new FakeRunner(), Privileged.MissingLstat).Access.PrepareSetupAsync(Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
    }

    // ---- running it ----

    private async Task<AdministratorSetupPlan> Plan(IAdministratorAccess access)
    {
        Bundle();
        return (await access.PrepareSetupAsync(Token)).Value!;
    }

    [LinuxFact]
    public async Task The_setup_is_one_pkexec_of_bash_with_the_compiled_in_script_the_two_files_and_their_hashes()
    {
        string lstat = "missing";
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Ops(Replace(runner, () => lstat = "trusted"), path => lstat == "trusted" ? Privileged.TrustedLstat(path) : Privileged.MissingLstat(path));
        AdministratorSetupPlan plan = await Plan(ops.Access);
        Assert.Equal(AdministratorAccessState.NeedsSetup, ops.Access.State);
        int changes = 0;
        ops.Access.StateChanged += () => changes++;

        PrivilegedResult<bool> result = await ops.Access.SetupAsync(plan, Token);

        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        RunCall call = Assert.Single(runner.Calls);
        Assert.Equal(Pkexec, call.File);
        Assert.Equal(
            [
                "/usr/bin/bash", "-c", Script, "pennyworth-install",
                "--helper", plan.HelperSource, "--helper-sha256", plan.HelperSha256,
                "--policy", plan.PolicySource, "--policy-sha256", plan.PolicySha256,
            ],
            call.Arguments);
        Assert.Null(call.Input);
        Assert.Equal(AdministratorAccessState.Off, ops.Access.State);
        Assert.Equal(1, changes);
    }

    /// <summary>A runner that answers success and runs <paramref name="after"/> (the install "happened").</summary>
    private static ElevatedRunnerFunc Replace(FakeRunner runner, Action after)
    {
        runner.Enqueue(Privileged.Exit(0));
        return (file, args, input, max, timeout, token) =>
        {
            Task<ElevatedRun> run = runner.Run(file, args, input, max, timeout, token);
            after();
            return run;
        };
    }

    [LinuxFact]
    public async Task After_the_setup_the_next_request_starts_the_session_with_its_own_single_prompt()
    {
        string lstat = "missing";
        var runner = new FakeRunner();
        var launcher = FakeLauncher.Serving(ServeFakes.Seams());
        LinuxPrivilegedOperations ops = new(
            Replace(runner, () => lstat = "trusted"),
            launcher.Launch,
            new[] { Pkexec, "/usr/bin/bash" }.Contains,
            path => lstat == "trusted" ? Privileged.TrustedLstat(path) : Privileged.MissingLstat(path),
            TimeSpan.FromSeconds(5),
            () => "",
            _ => null,
            new ManualTime(),
            AppFolder,
            () => Script);
        AdministratorSetupPlan plan = await Plan(ops.Access);

        await ops.Access.SetupAsync(plan, Token);
        Assert.Equal(0, launcher.Starts); // the setup starts nothing
        PrivilegedResult<IReadOnlyList<MeasuredFolder>> folders = await ops.MeasureFoldersAsync(["/root"], Token);

        Assert.Equal(PrivilegedOutcome.Done, folders.Outcome);
        Assert.Equal(1, launcher.Starts);
        Assert.Equal(AdministratorAccessState.Active, ops.Access.State);
    }

    [LinuxTheory]
    [InlineData(126, PrivilegedOutcome.Cancelled)]
    [InlineData(127, PrivilegedOutcome.Cancelled)]
    [InlineData(1, PrivilegedOutcome.Failed)]
    [InlineData(137, PrivilegedOutcome.Failed)]
    public async Task A_setup_that_was_declined_or_failed_changes_nothing_and_is_not_repeated(int exit, PrivilegedOutcome outcome)
    {
        var runner = new FakeRunner(Privileged.Exit(exit, error: "install-helper.sh: refusing: /usr/libexec is a symbolic link\nsecond line"));
        IAdministratorAccess access = Ops(runner, Privileged.MissingLstat).Access;
        AdministratorSetupPlan plan = await Plan(access);
        int changes = 0;
        access.StateChanged += () => changes++;

        PrivilegedResult<bool> result = await access.SetupAsync(plan, Token);

        Assert.Equal(outcome, result.Outcome);
        Assert.Single(runner.Calls);
        Assert.Equal(AdministratorAccessState.NeedsSetup, access.State);
        Assert.Equal(0, changes);
        if (exit == 1)
        {
            Assert.Equal("install-helper.sh: refusing: /usr/libexec is a symbolic link", result.Detail);
        }
    }

    [LinuxFact]
    public async Task A_setup_that_reports_success_but_leaves_no_trusted_helper_is_a_failure()
    {
        var runner = new FakeRunner(Privileged.Exit(0));
        IAdministratorAccess access = Ops(runner, Privileged.MissingLstat).Access;
        AdministratorSetupPlan plan = await Plan(access);

        PrivilegedResult<bool> result = await access.SetupAsync(plan, Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.Equal(AdministratorAccessState.NeedsSetup, access.State);
    }

    [LinuxFact]
    public async Task The_setup_never_runs_a_plan_that_installs_anywhere_but_the_fixed_path_or_without_bash_or_pkexec()
    {
        var runner = new FakeRunner();
        IAdministratorAccess access = Ops(runner, Privileged.MissingLstat).Access;
        AdministratorSetupPlan plan = await Plan(access);

        PrivilegedResult<bool> elsewhere = await access.SetupAsync(plan with { HelperDestination = "/tmp/pennyworth-helper" }, Token);
        PrivilegedResult<bool> noBash = await Ops(runner, Privileged.MissingLstat, installed: [Pkexec]).Access.SetupAsync(plan, Token);
        PrivilegedResult<bool> noPkexec = await Ops(runner, Privileged.MissingLstat, installed: ["/usr/bin/bash"]).Access.SetupAsync(plan, Token);
        PrivilegedResult<bool> noScript = await Ops(runner, Privileged.MissingLstat, script: () => null).Access.SetupAsync(plan, Token);

        Assert.Equal(PrivilegedOutcome.Failed, elsewhere.Outcome);
        Assert.Equal(PrivilegedOutcome.Failed, noBash.Outcome);
        Assert.Equal(PrivilegedUnavailableReason.NoPkexec, noPkexec.Reason);
        Assert.Equal(PrivilegedUnavailableReason.SetupNotBundled, noScript.Reason);
        Assert.Empty(runner.Calls);
    }

    [LinuxFact]
    public async Task The_setup_gets_the_long_wait_a_person_needs_and_cancelling_reaches_the_runner()
    {
        var runner = new FakeRunner(Privileged.Exit(126));
        IAdministratorAccess access = Ops(runner, Privileged.MissingLstat).Access;
        AdministratorSetupPlan plan = await Plan(access);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Token);

        await access.SetupAsync(plan, source.Token);

        RunCall call = Assert.Single(runner.Calls);
        Assert.True(call.Timeout > TimeSpan.FromSeconds(5));
        Assert.Equal(source.Token, call.Token);
    }
}
