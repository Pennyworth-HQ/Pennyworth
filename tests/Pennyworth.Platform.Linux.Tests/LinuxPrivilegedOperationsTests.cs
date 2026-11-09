using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pennyworth.Cleanup;
using Pennyworth.Core.Privilege;
using Pennyworth.KnowledgeBase;
using Pennyworth.Scanning;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>
/// What the app asks pkexec for and how it reads the answer. Real pkexec is never run: a fake plays it, and a fake channel (the real
/// helper loop over in-memory pipes) plays <c>pkexec &lt;helper&gt; serve</c>.
/// </summary>
public class LinuxPrivilegedOperationsTests
{
    private const string Pkexec = Privileged.Pkexec;
    private const string HelperPath = HelperProtocol.InstalledPath;

    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private static ElevatedRun Exit(int code, byte[]? output = null, string error = "") => Privileged.Exit(code, output, error);

    private static HelperSeams Seams(
        Func<CancellationToken, FirmwareTablesResponse>? firmware = null,
        Func<string, CancellationToken, SystemSpaceResponse>? space = null,
        Func<FoldersRequest, CancellationToken, FoldersResponse>? folders = null,
        Func<string, CleanRequest, CancellationToken, CleanResponse>? clean = null) =>
        ServeFakes.Seams(firmware: firmware, space: space, folders: folders, clean: clean);

    /// <summary>Operations with a trusted helper whose session serves with <paramref name="seams"/>; the launcher is returned to count starts.</summary>
    private static LinuxPrivilegedOperations Helper(FakeRunner runner, out FakeLauncher launcher, HelperSeams? seams = null, params string[] installed)
    {
        launcher = FakeLauncher.Serving(seams ?? Seams());
        return Privileged.Ops(runner, Privileged.TrustedLstat, installed.Length == 0 ? null : installed, launcher: launcher);
    }

    private static LinuxPrivilegedOperations Helper(FakeRunner runner, params string[] installed) => Helper(runner, out _, null, installed);

    private static LinuxPrivilegedOperations NoHelper(FakeRunner runner, params string[] installed) =>
        Privileged.Ops(runner, Privileged.MissingLstat, installed.Length == 0 ? null : installed);

    // ---- capability ----

    [Fact]
    public void Without_pkexec_nothing_can_be_asked()
    {
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, ["/usr/bin/cat"]);

        foreach (PrivilegedOperation operation in Enum.GetValues<PrivilegedOperation>())
        {
            Assert.Equal(PrivilegedCapability.Missing(PrivilegedUnavailableReason.NoPkexec), ops.Capability(operation));
        }
    }

    [Fact]
    public void The_alternative_pkexec_folder_is_found_too()
    {
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, ["/bin/pkexec"]);

        Assert.True(ops.Capability(PrivilegedOperation.FirmwareTables).Available);
    }

    [Fact]
    public void A_trusted_helper_makes_every_operation_available()
    {
        LinuxPrivilegedOperations ops = Helper(new FakeRunner());

        foreach (PrivilegedOperation operation in Enum.GetValues<PrivilegedOperation>())
        {
            Assert.Equal(PrivilegedCapability.Ready, ops.Capability(operation));
        }
    }

    [Theory]
    [InlineData("missing", PrivilegedUnavailableReason.HelperNotInstalled)]
    [InlineData("group-writable", PrivilegedUnavailableReason.HelperNotTrusted)]
    [InlineData("user-owned", PrivilegedUnavailableReason.HelperNotTrusted)]
    public void Without_a_trusted_helper_only_the_read_only_features_remain(string state, PrivilegedUnavailableReason reason)
    {
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), Lstat(state));

        Assert.True(ops.Capability(PrivilegedOperation.FirmwareTables).Available);
        Assert.True(ops.Capability(PrivilegedOperation.SystemSpace).Available);
        Assert.Equal(PrivilegedCapability.Missing(reason), ops.Capability(PrivilegedOperation.Folders));
        Assert.Equal(PrivilegedCapability.Missing(reason), ops.Capability(PrivilegedOperation.SystemCleanup));
    }

    [Fact]
    public void The_helper_is_judged_again_on_every_query()
    {
        string state = "trusted";
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), path => Lstat(state)(path));

        Assert.True(ops.Capability(PrivilegedOperation.SystemCleanup).Available);
        state = "group-writable";
        Assert.Equal(PrivilegedUnavailableReason.HelperNotTrusted, ops.Capability(PrivilegedOperation.SystemCleanup).Reason);
        state = "missing";
        Assert.Equal(PrivilegedUnavailableReason.HelperNotInstalled, ops.Capability(PrivilegedOperation.SystemCleanup).Reason);
        state = "trusted";
        Assert.True(ops.Capability(PrivilegedOperation.SystemCleanup).Available);
    }

    private static Func<string, EntryInfo> Lstat(string state) => state switch
    {
        "trusted" => Privileged.TrustedLstat,
        "missing" => Privileged.MissingLstat,
        "group-writable" => Privileged.GroupWritableLstat,
        "user-owned" => Privileged.UserOwnedLstat,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };



    [Fact]
    public async Task Without_cat_the_fallback_table_is_empty_and_nothing_is_run()
    {
        var runner = new FakeRunner();

        PrivilegedResult<byte[]> result = await NoHelper(runner, Pkexec).ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        Assert.Empty(result.Value!);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task The_memory_text_is_dmidecode_t_17_by_absolute_path()
    {
        var runner = new FakeRunner(Privileged.Text("Handle 0x0030, DMI type 17"));

        PrivilegedResult<string> result = await Helper(runner).ReadMemoryDevicesTextAsync(Token);

        Assert.Equal("Handle 0x0030, DMI type 17", result.Value);
        Assert.Equal([$"{Pkexec} /usr/sbin/dmidecode -t 17"], runner.Lines); // never through the helper, which does not offer it
    }

    [Fact]
    public async Task Without_dmidecode_it_fails_without_running_anything()
    {
        var runner = new FakeRunner();

        PrivilegedResult<string> result = await NoHelper(runner, Pkexec, "/usr/bin/cat").ReadMemoryDevicesTextAsync(Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.Empty(runner.Calls);
    }

    // ---- the session route ----

    [Fact]
    public async Task The_firmware_tables_come_from_the_helper_session_with_one_start_and_no_tool_run()
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Helper(runner, out FakeLauncher launcher, Seams(firmware: _ => new FirmwareTablesResponse(HelperProtocol.Version, Convert.ToBase64String([1, 2, 3, 4]))));

        PrivilegedResult<byte[]> result = await ops.ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        Assert.Equal([1, 2, 3, 4], result.Value);
        Assert.Equal([$"{Pkexec} {HelperPath} serve"], launcher.Lines);
        Assert.Empty(runner.Calls);
        Assert.Equal(AdministratorAccessState.Active, ops.Access.State);
    }

    [Fact]
    public async Task Every_operation_runs_in_the_one_session_and_the_password_is_asked_once()
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Helper(runner, out FakeLauncher launcher, Seams(
            space: (_, _) => new SystemSpaceResponse(HelperProtocol.Version, "btrfs", 1234, 5678)));

        PrivilegedResult<byte[]> firmware = await ops.ReadFirmwareTablesAsync(Token);
        PrivilegedResult<HiddenSpaceReport> space = await ops.MeasureSystemSpaceAsync("/", Token);
        PrivilegedResult<IReadOnlyList<MeasuredFolder>> folders = await ops.MeasureFoldersAsync(["/root", "/lost+found"], Token);
        PrivilegedResult<CleanupResult> clean = await ops.CleanSystemAsync(Estimate("system-package-cache", File("/var/cache/dnf/a", 1)), Token);

        Assert.All(new[] { firmware.Outcome, space.Outcome, folders.Outcome, clean.Outcome }, outcome => Assert.Equal(PrivilegedOutcome.Done, outcome));
        Assert.Equal(new HiddenSpaceReport(1234, 5678), space.Value);
        Assert.Equal(["/root", "/lost+found"], folders.Value!.Select(f => f.Path));
        Assert.Equal(1, launcher.Starts);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task System_space_null_parts_stay_null()
    {
        LinuxPrivilegedOperations ops = Helper(new FakeRunner(), out _, Seams(space: (_, _) => new SystemSpaceResponse(HelperProtocol.Version, "ext4", null, 42)));

        PrivilegedResult<HiddenSpaceReport> result = await ops.MeasureSystemSpaceAsync("/", Token);

        Assert.Equal(new HiddenSpaceReport(null, 42), result.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/with\0nul")]
    public async Task A_mount_point_that_is_not_an_absolute_path_is_never_passed_on(string mountPoint)
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Helper(runner, out FakeLauncher launcher);

        PrivilegedResult<HiddenSpaceReport> result = await ops.MeasureSystemSpaceAsync(mountPoint, Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.Empty(runner.Calls);
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task A_refusal_of_the_helper_is_a_failure_with_its_one_line_reason()
    {
        LinuxPrivilegedOperations ops = Helper(new FakeRunner(), out _, Seams(firmware: _ => throw new HelperException(HelperExit.Missing, "no table")));

        PrivilegedResult<byte[]> result = await ops.ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.Equal("no table", result.Detail);
        Assert.Equal(AdministratorAccessState.Active, ops.Access.State); // an answer, even a refusal, does not end the session
    }

    [Theory]
    [InlineData("missing", PrivilegedUnavailableReason.HelperNotInstalled)]
    [InlineData("group-writable", PrivilegedUnavailableReason.HelperNotTrusted)]
    public async Task Folders_and_cleanup_never_fall_back_to_anything_else(string state, PrivilegedUnavailableReason reason)
    {
        var runner = new FakeRunner();
        var launcher = new FakeLauncher();
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Lstat(state), launcher: launcher);

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> folders = await ops.MeasureFoldersAsync(["/root"], Token);
        PrivilegedResult<CleanupResult> clean = await ops.CleanSystemAsync(Estimate("system-crash-reports", File("/var/crash/a", 1)), Token);

        Assert.Equal((PrivilegedOutcome.NotAvailable, reason), (folders.Outcome, folders.Reason));
        Assert.Equal((PrivilegedOutcome.NotAvailable, reason), (clean.Outcome, clean.Reason));
        Assert.Empty(runner.Calls);
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task Folders_and_cleanup_without_pkexec_are_not_available()
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Privileged.TrustedLstat, ["/usr/bin/cat"]);

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> folders = await ops.MeasureFoldersAsync(["/root"], Token);
        PrivilegedResult<CleanupResult> clean = await ops.CleanSystemAsync(Estimate("system-crash-reports", File("/var/crash/a", 1)), Token);

        Assert.Equal(PrivilegedUnavailableReason.NoPkexec, folders.Reason);
        Assert.Equal(PrivilegedUnavailableReason.NoPkexec, clean.Reason);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task The_firmware_tables_fall_back_to_cat_only_when_the_helper_is_not_usable_and_say_nothing_of_a_session()
    {
        foreach (string state in new[] { "missing", "group-writable", "user-owned" })
        {
            var runner = new FakeRunner(Exit(0, [5, 6]));
            var launcher = new FakeLauncher();
            LinuxPrivilegedOperations ops = Privileged.Ops(runner, Lstat(state), launcher: launcher);

            PrivilegedResult<byte[]> result = await ops.ReadFirmwareTablesAsync(Token);

            Assert.Equal([5, 6], result.Value);
            RunCall call = Assert.Single(runner.Calls);
            Assert.Equal(Pkexec, call.File);
            Assert.Equal(["/usr/bin/cat", "/sys/firmware/dmi/tables/DMI"], call.Arguments);
            Assert.Null(call.Input);
            Assert.Equal(ElevatedRunner.MaxOutput, call.MaxOutput);
            Assert.Equal(0, launcher.Starts);
            Assert.Equal(AdministratorAccessState.NeedsSetup, ops.Access.State); // a one-off read did not turn anything on
        }
    }

    [Fact]
    public async Task A_background_refresh_never_prompts_and_uses_a_running_session()
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Helper(runner, out FakeLauncher launcher);

        PrivilegedResult<byte[]> cold = await ops.ReadFirmwareTablesAsync(Token, promptIfNeeded: false);
        PrivilegedResult<IReadOnlyList<MeasuredFolder>> coldFolders = await ops.MeasureFoldersAsync(["/root"], Token, promptIfNeeded: false);
        PrivilegedResult<HiddenSpaceReport> coldSpace = await ops.MeasureSystemSpaceAsync("/", Token, promptIfNeeded: false);

        Assert.All(new[] { cold.Reason, coldFolders.Reason, coldSpace.Reason }, reason => Assert.Equal(PrivilegedUnavailableReason.NotActive, reason));
        Assert.Equal(0, launcher.Starts);

        await ops.Access.TurnOnAsync(Token);
        PrivilegedResult<IReadOnlyList<MeasuredFolder>> warm = await ops.MeasureFoldersAsync(["/root"], Token, promptIfNeeded: false);

        Assert.Equal(PrivilegedOutcome.Done, warm.Outcome);
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task A_background_refresh_does_not_fall_back_to_a_one_off_prompt_either()
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = NoHelper(runner);

        PrivilegedResult<byte[]> table = await ops.ReadFirmwareTablesAsync(Token, promptIfNeeded: false);
        PrivilegedResult<HiddenSpaceReport> space = await ops.MeasureSystemSpaceAsync("/", Token, promptIfNeeded: false);

        Assert.Equal(PrivilegedUnavailableReason.NotActive, table.Reason);
        Assert.Equal(PrivilegedUnavailableReason.NotActive, space.Reason);
        Assert.Empty(runner.Calls);
    }

    // ---- folders ----

    [Fact]
    public async Task Folders_are_sent_in_one_request_and_mapped_back()
    {
        FoldersRequest? seen = null;
        var response = new FoldersResponse(HelperProtocol.Version, [new FolderSize("/root", 4096, 100, 3, true), new FolderSize("/var/lib/x", 10, 9, 1, false)]);
        LinuxPrivilegedOperations ops = Helper(new FakeRunner(), out _, Seams(folders: (request, _) =>
        {
            seen = request;
            return response;
        }));

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> result = await ops.MeasureFoldersAsync(["/root", "/var/lib/x"], Token);

        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        Assert.Equal([new MeasuredFolder("/root", 4096, 100, 3, true), new MeasuredFolder("/var/lib/x", 10, 9, 1, false)], result.Value);
        Assert.Equal(["/root", "/var/lib/x"], seen!.Paths);
    }

    [Fact]
    public async Task Paths_that_are_not_absolute_or_repeat_are_not_sent_and_nothing_to_measure_asks_for_nothing()
    {
        var sent = new List<IReadOnlyList<string>>();
        LinuxPrivilegedOperations ops = Helper(new FakeRunner(), out FakeLauncher launcher, Seams(folders: (request, _) =>
        {
            sent.Add(request.Paths);
            return new FoldersResponse(HelperProtocol.Version, []);
        }));

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> none = await ops.MeasureFoldersAsync(["relative"], Token);
        Assert.Equal(0, launcher.Starts);
        await ops.MeasureFoldersAsync(["relative", "", "/a\0b", "/root", "/root"], Token);

        Assert.Equal(["/root"], Assert.Single(sent));
        Assert.Equal(PrivilegedOutcome.Done, none.Outcome);
        Assert.Empty(none.Value!);
    }

    [Fact]
    public async Task More_folders_than_one_request_holds_go_in_further_requests_of_the_same_session()
    {
        string[] paths = [.. Enumerable.Range(0, HelperProtocol.MaxFolders + 1).Select(i => $"/data/{i}")];
        var counts = new List<int>();
        LinuxPrivilegedOperations ops = Helper(new FakeRunner(), out FakeLauncher launcher, Seams(folders: (request, _) =>
        {
            counts.Add(request.Paths.Count);
            return new FoldersResponse(HelperProtocol.Version, [new FolderSize(request.Paths[0], 1, 1, 1, true)]);
        }));

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> result = await ops.MeasureFoldersAsync(paths, Token);

        Assert.Equal(["/data/0", "/data/512"], result.Value!.Select(f => f.Path));
        Assert.Equal([HelperProtocol.MaxFolders, 1], counts);
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task A_dismissed_first_prompt_stops_the_rest_and_is_not_asked_again()
    {
        string[] paths = [.. Enumerable.Range(0, HelperProtocol.MaxFolders + 1).Select(i => $"/data/{i}")];
        var launcher = new FakeLauncher(() => FakeChannel.Exits(126));
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, launcher: launcher);

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> result = await ops.MeasureFoldersAsync(paths, Token);

        Assert.Equal(PrivilegedOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, launcher.Starts);
    }

    // ---- cleanup ----

    // ---- cleanup ----

    private static CleanupRule Rule(string id, bool requiresAdmin = true) =>
        new(id, SafetyLevel.Safe, CleanupKind.FolderContents, [], requiresAdmin, new LocalizedText("t", "t"), new LocalizedText("d", "d"), new LocalizedText("c", "c"));

    private static CleanupFile File(string path, long size, long ticks = 638_000_000_000_000_000) => new(path, size, new DateTime(ticks, DateTimeKind.Utc));

    private static CleanupEstimate Estimate(string ruleId, params CleanupFile[] files) =>
        new(Rule(ruleId), files.Sum(f => f.Size), files.Length, true, Files: files);

    [Fact]
    public async Task Cleaning_sends_the_preview_with_the_rule_through_the_session()
    {
        string? rule = null;
        CleanRequest? seen = null;
        LinuxPrivilegedOperations ops = Helper(new FakeRunner(), out FakeLauncher launcher, Seams(clean: (id, request, _) =>
        {
            rule = id;
            seen = request;
            return new CleanResponse(HelperProtocol.Version, 300, 2, 1);
        }));
        CleanupEstimate estimate = Estimate("system-package-cache", File("/var/cache/zypp/packages/repo/a.rpm", 100, 111), File("/var/cache/apt/archives/b.deb", 200, 222), File("/var/cache/apt/archives/partial/c", 5));

        PrivilegedResult<CleanupResult> result = await ops.CleanSystemAsync(estimate, Token);

        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        Assert.Equal(new CleanupResult(estimate.Rule, 300, 2, 1 + 1), result.Value); // the helper's skipped plus the kept apt folder never sent
        Assert.Equal("system-package-cache", rule);
        Assert.Equal(HelperProtocol.Version, seen!.Version);
        Assert.Equal(
            [
                new CleanFile("/var/cache/zypp/packages", "repo/a.rpm", 100, 111),
                new CleanFile("/var/cache/apt/archives", "b.deb", 200, 222),
            ],
            seen.Files);
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task Only_a_rule_the_helper_knows_that_needs_admin_is_ever_cleaned()
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Helper(runner, out FakeLauncher launcher);

        PrivilegedResult<CleanupResult> unknown = await ops.CleanSystemAsync(Estimate("browser-cache", File("/var/crash/a", 1)), Token);
        PrivilegedResult<CleanupResult> unprivileged = await ops.CleanSystemAsync(new CleanupEstimate(Rule("system-crash-reports", requiresAdmin: false), 1, 1, true, Files: [File("/var/crash/a", 1)]), Token);

        Assert.Equal(PrivilegedOutcome.Failed, unknown.Outcome);
        Assert.Equal(PrivilegedOutcome.Failed, unprivileged.Outcome);
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public void Each_file_goes_with_the_allowlisted_root_that_is_its_exact_prefix()
    {
        string[] roots = ["/var/cache/zypp/packages", "/var/cache/dnf"];
        CleanupFile[] preview =
        [
            File("/var/cache/zypp/packages/x.rpm", 1),
            File("/var/cache/dnf/repo/y.rpm", 2),
            File("/var/cache/dnfx/z", 3), // a different folder that shares a prefix
            File("/var/cache/dnf", 4), // the root itself
            File("/var/cache/dnf/", 5), // nothing below it
            File("/etc/passwd", 6),
            File("/var/cache/zypp/packages/../../../etc/shadow", 7),
            File("/var/cache/dnf/a//b", 8),
            File("/var/cache/dnf/./c", 9),
            File("relative/path", 10),
        ];

        (List<CleanFile> files, _, long notSent) = LinuxPrivilegedOperations.Prepare(preview, roots);

        Assert.Equal([new CleanFile("/var/cache/zypp/packages", "x.rpm", 1, File("", 1).ChangedUtc.Ticks), new CleanFile("/var/cache/dnf", "repo/y.rpm", 2, File("", 1).ChangedUtc.Ticks)], files);
        Assert.Equal(8, notSent);
    }

    [Fact]
    public void Apts_own_working_files_are_never_sent()
    {
        string[] roots = ["/var/cache/apt/archives"];
        CleanupFile[] preview =
        [
            File("/var/cache/apt/archives/lock", 1),
            File("/var/cache/apt/archives/partial/x.deb", 2),
            File("/var/cache/apt/archives/partial", 3),
            File("/var/cache/apt/archives/locked.deb", 4),
            File("/var/cache/apt/archives/pkg/lock", 5),
        ];

        (List<CleanFile> files, _, long notSent) = LinuxPrivilegedOperations.Prepare(preview, roots);

        Assert.Equal(["locked.deb", "pkg/lock"], files.Select(f => f.Path));
        Assert.Equal(3, notSent);
    }

    [Fact]
    public void A_name_that_is_not_text_is_not_sent()
    {
        string[] roots = ["/var/crash"];
        CleanupFile[] preview =
        [
            File("/var/crash/caf\u00e9.crash", 1), // ordinary non-ASCII is fine
            File("/var/crash/bad\uFFFD.crash", 2), // what .NET shows for bytes that are not UTF-8
            File("/var/crash/lone\uD800.crash", 3), // a lone surrogate would be written as U+FFFD
            File("/var/crash/nul\0.crash", 4),
        ];

        (List<CleanFile> files, byte[] request, long notSent) = LinuxPrivilegedOperations.Prepare(preview, roots);

        Assert.Equal(["caf\u00e9.crash"], files.Select(f => f.Path));
        Assert.Equal(3, notSent);
        Assert.Equal(["caf\u00e9.crash"], Privileged.Parse(request, HelperJson.Default.CleanRequest).Files.Select(f => f.Path));
    }

    [Fact]
    public void No_more_than_the_most_files_a_request_may_list_are_sent()
    {
        string[] roots = ["/var/crash"];
        CleanupFile[] preview = [.. Enumerable.Range(0, HelperProtocol.MaxCleanFiles + 7).Select(i => File($"/var/crash/{i}", 1))];

        (List<CleanFile> files, byte[] request, long notSent) = LinuxPrivilegedOperations.Prepare(preview, roots);

        Assert.Equal(HelperProtocol.MaxCleanFiles, files.Count);
        Assert.Equal(7, notSent);
        Assert.Equal(HelperProtocol.MaxCleanFiles, Privileged.Parse(request, HelperJson.Default.CleanRequest).Files.Count);
        Assert.Equal("0", files[0].Path); // the first ones are kept, in order
    }

    [Fact]
    public void A_preview_too_big_for_the_request_is_cut_to_what_fits()
    {
        string[] roots = ["/var/crash"];
        string name = new('n', 3000);
        CleanupFile[] preview = [.. Enumerable.Range(0, 12_000).Select(i => File($"/var/crash/{i}{name}", 1))]; // about 36 MB of names

        (List<CleanFile> files, byte[] request, long notSent) = LinuxPrivilegedOperations.Prepare(preview, roots);

        Assert.InRange(request.Length, 1, HelperProtocol.MaxRequestBytes);
        Assert.InRange(files.Count, 1, 11_999);
        Assert.Equal(preview.Length - files.Count, notSent);
        Assert.Equal(files.Count, Privileged.Parse(request, HelperJson.Default.CleanRequest).Files.Count);
    }

    [Fact]
    public async Task A_preview_with_nothing_to_send_asks_for_no_password()
    {
        LinuxPrivilegedOperations ops = Helper(new FakeRunner(), out FakeLauncher launcher);
        CleanupEstimate estimate = Estimate("system-crash-reports", File("/etc/passwd", 5), File("/var/crash/../x", 6));

        PrivilegedResult<CleanupResult> result = await ops.CleanSystemAsync(estimate, Token);

        Assert.Equal(new CleanupResult(estimate.Rule, 0, 0, 2), result.Value);
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task A_refusing_helper_is_a_failure_with_its_reason()
    {
        LinuxPrivilegedOperations ops = Helper(new FakeRunner(), out _, Seams(clean: (_, _, _) => throw new HelperException(HelperExit.Refused, "/var/crash is not owned by root")));

        PrivilegedResult<CleanupResult> result = await ops.CleanSystemAsync(Estimate("system-crash-reports", File("/var/crash/a", 1)), Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.Equal("/var/crash is not owned by root", result.Detail);
    }

    [Fact]
    public async Task The_admin_cleaner_forwards_to_the_operations()
    {
        var cleaner = new LinuxAdminCleaner(Helper(new FakeRunner(), out _, Seams(clean: (_, _, _) => new CleanResponse(HelperProtocol.Version, 1, 1, 0))));
        CleanupEstimate estimate = Estimate("system-crash-reports", File("/var/crash/a", 1));

        PrivilegedResult<CleanupResult> result = await cleaner.CleanAsync(estimate, Token);

        Assert.Equal(PrivilegedCapability.Ready, cleaner.Capability);
        Assert.Equal(new CleanupResult(estimate.Rule, 1, 1, 0), result.Value);
    }

    [Fact]
    public void The_admin_cleaners_capability_is_the_cleanup_capability()
    {
        Assert.Equal(PrivilegedUnavailableReason.HelperNotInstalled, new LinuxAdminCleaner(Privileged.Ops(new FakeRunner(), Privileged.MissingLstat)).Capability.Reason);
        Assert.Equal(PrivilegedUnavailableReason.NoPkexec, new LinuxAdminCleaner(Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, [])).Capability.Reason);
    }

    // ---- the outcome matrix: what pkexec's one-off tool runs say ----

    [Theory]
    [InlineData(126, PrivilegedOutcome.Cancelled)]
    [InlineData(127, PrivilegedOutcome.Cancelled)]
    [InlineData(137, PrivilegedOutcome.Failed)]
    public async Task The_same_pkexec_codes_settle_a_fallback_run(int exitCode, PrivilegedOutcome outcome)
    {
        var runner = new FakeRunner(Exit(exitCode));

        PrivilegedResult<string> result = await NoHelper(runner).ReadMemoryDevicesTextAsync(Token);

        Assert.Equal(outcome, result.Outcome);
        Assert.Single(runner.Calls);
    }

    // ---- the protocol ----

    [Fact]
    public async Task The_session_is_started_with_the_fixed_arguments_and_nothing_else()
    {
        LinuxPrivilegedOperations ops = Helper(new FakeRunner(), out FakeLauncher launcher);

        await ops.Access.TurnOnAsync(Token);

        Assert.Equal([$"{Pkexec} {HelperPath} serve"], launcher.Lines);
    }

    [Fact]
    public void The_helper_path_is_the_one_the_polkit_actions_name()
    {
        Assert.Equal("/usr/libexec/pennyworth/pennyworth-helper", HelperProtocol.InstalledPath);
        Assert.Equal(["read-firmware-tables", "measure-system-space", "measure-folders", "clean-system", "serve"], HelperProtocol.Subcommands);
    }

    [Fact]
    public void Every_message_survives_the_round_trip_through_json()
    {
        Assert.Equal(new FirmwareTablesResponse(1, "AQID"), RoundTrip(new FirmwareTablesResponse(1, "AQID"), HelperJson.Default.FirmwareTablesResponse));
        Assert.Equal(new SystemSpaceResponse(1, "btrfs", 5, null), RoundTrip(new SystemSpaceResponse(1, "btrfs", 5, null), HelperJson.Default.SystemSpaceResponse));
        Assert.Equal(new SystemSpaceResponse(1, "ext4", null, 7), RoundTrip(new SystemSpaceResponse(1, "ext4", null, 7), HelperJson.Default.SystemSpaceResponse));

        var folders = new FoldersRequest(1, ["/root", "/var/lib/caf\u00e9"]);
        Assert.Equal(folders.Paths, RoundTrip(folders, HelperJson.Default.FoldersRequest).Paths);

        var sizes = new FoldersResponse(1, [new FolderSize("/root", 4096, 100, 3, true), new FolderSize("/x", 1, 1, 1, false)]);
        Assert.Equal(sizes.Folders, RoundTrip(sizes, HelperJson.Default.FoldersResponse).Folders);

        var request = new CleanRequest(1, [new CleanFile("/var/crash", "a/b.crash", 10, 638_000_000_000_000_000)]);
        Assert.Equal(request.Files, RoundTrip(request, HelperJson.Default.CleanRequest).Files);

        Assert.Equal(new CleanResponse(1, 10, 2, 3), RoundTrip(new CleanResponse(1, 10, 2, 3), HelperJson.Default.CleanResponse));
    }

    [Fact]
    public void The_wire_names_are_camelCase_and_the_ticks_are_a_plain_number()
    {
        string request = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new CleanRequest(1, [new CleanFile("/var/crash", "a", 10, 638000000000000000)]), HelperJson.Default.CleanRequest));
        string space = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new SystemSpaceResponse(1, "btrfs", null, 2), HelperJson.Default.SystemSpaceResponse));

        Assert.Equal("""{"version":1,"files":[{"root":"/var/crash","path":"a","size":10,"modifiedTicks":638000000000000000}]}""", request);
        Assert.Equal("""{"version":1,"fileSystem":"btrfs","snapshotBytes":null,"metadataBytes":2}""", space);
    }

    private static T RoundTrip<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, type), type)!;


    [Fact]
    public void A_request_and_an_answer_of_a_session_survive_json_and_use_the_same_wire_names()
    {
        var request = new ServeRequest(1, 9, "clean-system", "system-crash-reports", Files: [new CleanFile("/var/crash", "a", 10, 638000000000000000)]);
        var response = new ServeResponse(1, 9, 0, null, Clean: new CleanResponse(1, 10, 2, 3));

        Assert.Equal(request.Files, RoundTrip(request, HelperJson.Default.ServeRequest).Files);
        Assert.Equal(response.Clean, RoundTrip(response, HelperJson.Default.ServeResponse).Clean);
        Assert.Equal(
            """{"version":1,"id":9,"op":"hello","argument":null,"paths":null,"files":null}""",
            Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new ServeRequest(1, 9, "hello"), HelperJson.Default.ServeRequest)));
    }

    private const string BtrfsMountInfo = """
        43 1 0:39 / / rw,relatime shared:1 - btrfs /dev/nvme0n1p2 rw,subvolid=5,subvol=/
        54 43 259:1 / /home rw,relatime shared:47 - ext4 /dev/nvme0n1p1 rw
        """;

    private const string BtrfsUsage = """
        Overall:
            Metadata ratio:		              2.00

        Data,single: Size:153553469440, Used:147889410048 (96.31%)

        Metadata,DUP: Size:5368709120, Used:4420976640 (82.35%)

        System,DUP: Size:33554432, Used:16384 (0.05%)

        """;

    private static readonly VfsCounters Counters = new(4096, 4096, 66609152, 28299139, 27865259);

    private static readonly long Reserved = (28299139L - 27865259L) * 4096;

    [Fact]
    public async Task Without_the_helper_btrfs_space_is_measured_by_three_fixed_btrfs_commands()
    {
        var runner = new FakeRunner(Privileged.Text(BtrfsUsage), Exit(1, error: "ERROR: quotas not enabled"), Privileged.Text(""));
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Privileged.MissingLstat, mountInfo: BtrfsMountInfo, counters: Counters);

        PrivilegedResult<HiddenSpaceReport> result = await ops.MeasureSystemSpaceAsync("/", Token);

        Assert.Equal(
            [
                $"{Pkexec} /usr/bin/btrfs filesystem usage -b /",
                $"{Pkexec} /usr/bin/btrfs qgroup show -pcre --raw /",
                $"{Pkexec} /usr/bin/btrfs subvolume list -s /",
            ],
            runner.Lines);
        Assert.All(runner.Calls, call => Assert.Null(call.Input));
        Assert.Equal(new HiddenSpaceReport(null, Reserved + ((4420976640L + 16384L) * 2)), result.Value); // quotas off: only the snapshots are unknown
    }

    [Fact]
    public async Task The_btrfs_report_is_the_one_the_helper_builds_from_the_same_text()
    {
        const string qgroup = "Qgroupid Referenced Exclusive Parent Child\n0/257 100 100 --- ---\n";
        const string list = "ID 257 gen 1 cgen 1 top level 5 otime 2024-01-01 00:00:00 path snap\n";
        var runner = new FakeRunner(Privileged.Text(BtrfsUsage), Privileged.Text(qgroup), Privileged.Text(list));
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Privileged.UserOwnedLstat, mountInfo: BtrfsMountInfo, counters: Counters);

        PrivilegedResult<HiddenSpaceReport> result = await ops.MeasureSystemSpaceAsync("/", Token);

        SystemSpaceResponse expected = BtrfsSpace.Build("btrfs", Reserved, BtrfsUsage, list, qgroup);
        Assert.Equal(new HiddenSpaceReport(expected.SnapshotBytes, expected.MetadataBytes), result.Value);
    }

    [Fact]
    public async Task Without_the_helper_a_file_system_that_is_not_btrfs_needs_no_prompt()
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Privileged.MissingLstat, mountInfo: BtrfsMountInfo, counters: Counters);

        PrivilegedResult<HiddenSpaceReport> result = await ops.MeasureSystemSpaceAsync("/home/user", Token);

        Assert.Equal(new HiddenSpaceReport(null, Reserved), result.Value);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_dismissed_btrfs_prompt_ends_the_measurement_after_one_prompt()
    {
        var runner = new FakeRunner(Exit(126));
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Privileged.MissingLstat, mountInfo: BtrfsMountInfo, counters: Counters);

        PrivilegedResult<HiddenSpaceReport> result = await ops.MeasureSystemSpaceAsync("/", Token);

        Assert.Equal(PrivilegedOutcome.Cancelled, result.Outcome);
        Assert.Single(runner.Calls);
    }

    // ---- the process ----

    private static SessionLauncher Real(string program, params string[] arguments) => (_, _) => PkexecChannel.Launch(program, arguments);

    [LinuxFact]
    public async Task Cancelling_during_the_prompt_kills_pkexec_and_returns_at_once()
    {
        using var source = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, timeout: TimeSpan.FromSeconds(60), launch: Real("/bin/sleep", "30"));

        Task<PrivilegedResult<byte[]>> pending = ops.ReadFirmwareTablesAsync(source.Token);
        await Task.Delay(200, Token);
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed}");
        Assert.Equal(AdministratorAccessState.Off, ops.Access.State);
    }

    [LinuxFact]
    public async Task A_prompt_nobody_answers_ends_at_the_timeout_as_a_failure()
    {
        var clock = Stopwatch.StartNew();
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, timeout: TimeSpan.FromMilliseconds(300), launch: Real("/bin/sleep", "30"));

        PrivilegedResult<byte[]> result = await ops.ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed}");
    }

    [LinuxFact]
    public async Task A_process_that_ends_before_answering_is_classified_by_its_exit_code_through_the_real_channel()
    {
        LinuxPrivilegedOperations dismissed = Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, timeout: TimeSpan.FromSeconds(30), launch: Real("/bin/sh", "-c", "exit 126"));
        LinuxPrivilegedOperations agentless = Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, timeout: TimeSpan.FromSeconds(30), launch: Real("/bin/sh", "-c", "echo 'Error getting authority: No authentication agent found.' >&2; exit 127"));

        PrivilegedResult<byte[]> first = await dismissed.ReadFirmwareTablesAsync(Token);
        PrivilegedResult<byte[]> second = await agentless.ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Cancelled, first.Outcome);
        Assert.Equal(PrivilegedUnavailableReason.NoAuthenticationAgent, second.Reason);
    }

    [LinuxFact]
    public async Task An_answer_header_over_the_limit_is_a_failure()
    {
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, timeout: TimeSpan.FromSeconds(30), launch: Real("/bin/sh", "-c", "printf '\\377\\377\\377\\377'; sleep 30"));

        PrivilegedResult<byte[]> result = await ops.ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
    }
}
