using System.Globalization;
using Pennyworth.App.Resources;
using Pennyworth.App.ViewModels;
using Pennyworth.Cleanup;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Privilege;
using Pennyworth.Core.Storage;
using Pennyworth.Monitoring.Hardware;
using Pennyworth.Platform;
using Pennyworth.Scanning;
using static Pennyworth.App.Tests.PrivilegedUiTests;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

/// <summary>
/// The one "Administrator access" concept in the window: Off, Active, Needs setup; turning it on with one prompt, locking it,
/// the one-time setup, and everything that happens when it becomes active (the locked folders are measured and shown, the system
/// space includes the administrator-only parts, the memory modules fill in, root-owned cleanup rules become usable). All against fakes.
/// </summary>
[Collection(nameof(LanguageSwitching))]
public class AdministratorAccessUiTests
{
    private static readonly string Secret = Path.Combine(@"C:\", "Secret");

    private static FakePrivileged Ops(AdministratorAccessState state = AdministratorAccessState.Off, PrivilegedUnavailableReason? reason = null) => new()
    {
        Access = new FakeAdministratorAccess { State = state, Reason = reason },
        FoldersResult = paths => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([new MeasuredFolder(paths[0], 10 * Gb, 10 * Gb, 42, Complete: true)]),
    };

    private static FakePrivileged NeedsSetup(PrivilegedUnavailableReason reason = PrivilegedUnavailableReason.HelperNotInstalled) => new()
    {
        Access = new FakeAdministratorAccess { State = AdministratorAccessState.NeedsSetup, Reason = reason },
        FoldersCapability = PrivilegedCapability.Missing(reason),
    };

    // ---- the status ----

    [Fact]
    public async Task Where_the_platform_has_no_administrator_access_nothing_of_it_shows()
    {
        MainWindowViewModel viewModel = await Scanned(privileged: null);

        Assert.False(viewModel.Access.IsSupported);
        Assert.True(viewModel.Access.IsUnavailable);
        viewModel.RequestTurnOn();
        Assert.Null(viewModel.Dialog);
        viewModel.LockAccess();
        Assert.Null(viewModel.Toast);
    }

    [Theory]
    [InlineData(AdministratorAccessState.Off, "Off")]
    [InlineData(AdministratorAccessState.Active, "Active")]
    [InlineData(AdministratorAccessState.NeedsSetup, "Needs setup")]
    [InlineData(AdministratorAccessState.Unavailable, "Not available")]
    public async Task The_status_says_where_access_stands(AdministratorAccessState state, string english)
    {
        MainWindowViewModel viewModel = await Scanned(Ops(state, state == AdministratorAccessState.Unavailable ? PrivilegedUnavailableReason.NoPkexec : null));
        AdminAccessViewModel access = viewModel.Access;

        Assert.True(access.IsSupported);
        Assert.Equal(state, access.State);
        Assert.Equal((state == AdministratorAccessState.Off, state == AdministratorAccessState.Active, state == AdministratorAccessState.NeedsSetup, state == AdministratorAccessState.Unavailable), (access.IsOff, access.IsActive, access.NeedsSetup, access.IsUnavailable));

        Assert.Equal($"Administrator access: {english}", access.StatusText);
        Assert.NotEmpty(access.Tip);
        Assert.StartsWith("Acesso de administrador", Strings.ResourceManager.GetString("AccessTitle", CultureInfo.GetCultureInfo("pt-BR")));
    }

    [Fact]
    public async Task The_tip_names_the_idle_time_while_active_and_the_reason_when_unavailable()
    {
        MainWindowViewModel active = await Scanned(Ops(AdministratorAccessState.Active));
        MainWindowViewModel none = await Scanned(Ops(AdministratorAccessState.Unavailable, PrivilegedUnavailableReason.NoPkexec));

        Assert.Contains("15", active.Access.Tip);
        Assert.Equal(Strings.PrivReasonNoPkexec, none.Access.Tip);
    }

    // ---- turning it on ----

    [Fact]
    public async Task The_status_when_off_explains_what_the_access_is_for_and_then_asks_once()
    {
        FakePrivileged ops = Ops();
        MainWindowViewModel viewModel = await Scanned(ops);

        viewModel.Access.PrimaryCommand.Execute(null);
        var dialog = Assert.IsType<AccessDialogViewModel>(viewModel.Dialog);

        Assert.Contains("15", dialog.Why);
        Assert.NotEmpty(dialog.Reads);
        Assert.NotEmpty(dialog.Wont);
        Assert.Equal(Strings.AccessTurnOn, dialog.AskText);
        Assert.Equal(0, ops.Access.TurnOnCalls); // reading the explanation asks for nothing

        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Null(viewModel.Dialog);
        Assert.Equal(1, ops.Access.TurnOnCalls);
        Assert.True(viewModel.Access.IsActive);
        Assert.Equal(string.Format(Strings.AccessOnToast, 15), viewModel.Toast);
    }

    [Fact]
    public async Task A_dismissed_prompt_is_cancelled_and_is_not_asked_again_by_itself()
    {
        FakePrivileged ops = Ops();
        ops.Access.TurnOnResult = PrivilegedResult<bool>.Cancelled;
        MainWindowViewModel viewModel = await Scanned(ops);
        viewModel.RequestTurnOn();
        var dialog = Assert.IsType<AccessDialogViewModel>(viewModel.Dialog);

        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(PrivilegedDialogState.Cancelled, dialog.State);
        Assert.Equal(Strings.PrivCancelled, dialog.Message);
        Assert.Equal(1, ops.Access.TurnOnCalls);
        Assert.True(viewModel.Access.IsOff);

        dialog.TryAgainCommand.Execute(null);
        Assert.Equal(PrivilegedDialogState.Asking, dialog.State);
        Assert.Equal(1, ops.Access.TurnOnCalls); // asking again takes another press
    }

    [Theory]
    [InlineData(PrivilegedUnavailableReason.NoAuthenticationAgent)]
    [InlineData(PrivilegedUnavailableReason.HelperOutdated)]
    public async Task A_prompt_that_cannot_be_shown_says_why(PrivilegedUnavailableReason reason)
    {
        FakePrivileged ops = Ops();
        ops.Access.TurnOnResult = PrivilegedResult<bool>.NotAvailable(reason);
        MainWindowViewModel viewModel = await Scanned(ops);
        viewModel.RequestTurnOn();
        var dialog = Assert.IsType<AccessDialogViewModel>(viewModel.Dialog);

        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(PrivilegedDialogState.Unavailable, dialog.State);
        Assert.Equal(PrivilegedText.Reason(reason), dialog.Message);
    }

    [Fact]
    public async Task A_failure_to_turn_on_is_one_calm_sentence()
    {
        FakePrivileged ops = Ops();
        ops.Access.TurnOnResult = PrivilegedResult<bool>.Failed("boom");
        MainWindowViewModel viewModel = await Scanned(ops);
        viewModel.RequestTurnOn();
        var dialog = Assert.IsType<AccessDialogViewModel>(viewModel.Dialog);

        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(PrivilegedDialogState.Failed, dialog.State);
        Assert.Equal(Strings.PrivFailed, dialog.Message);
    }

    [Fact]
    public async Task Nothing_offers_to_turn_on_what_is_already_on_or_cannot_be_turned_on()
    {
        MainWindowViewModel active = await Scanned(Ops(AdministratorAccessState.Active));
        active.RequestTurnOn();
        Assert.Null(active.Dialog);

        MainWindowViewModel needsSetup = await Scanned(NeedsSetup());
        needsSetup.RequestTurnOn();
        Assert.Null(needsSetup.Dialog);
    }

    // ---- locking and the end of a session ----

    [Fact]
    public async Task Lock_ends_the_session_and_says_so()
    {
        FakePrivileged ops = Ops(AdministratorAccessState.Active);
        MainWindowViewModel viewModel = await Scanned(ops);

        viewModel.Access.LockCommand.Execute(null);

        Assert.Equal(1, ops.Access.LockCalls);
        Assert.True(viewModel.Access.IsOff);
        Assert.Equal(Strings.AccessLockedToast, viewModel.Toast);
    }

    [Fact]
    public async Task A_session_that_ended_by_itself_says_the_next_action_asks_again()
    {
        FakePrivileged ops = Ops(AdministratorAccessState.Active);
        MainWindowViewModel viewModel = await Scanned(ops);

        ops.Access.Set(AdministratorAccessState.Off); // the idle lock, or a crash

        Assert.Equal(Strings.AccessEndedToast, viewModel.Toast);
        Assert.True(viewModel.Access.IsOff);
    }

    [Fact]
    public async Task Lock_does_nothing_when_nothing_is_on()
    {
        FakePrivileged ops = Ops();
        MainWindowViewModel viewModel = await Scanned(ops);

        viewModel.LockAccess();

        Assert.Equal(0, ops.Access.LockCalls);
    }

    // ---- the setup ----

    [Fact]
    public async Task The_status_when_it_needs_setup_shows_exactly_what_will_be_installed_before_anything_runs()
    {
        FakePrivileged ops = NeedsSetup();
        MainWindowViewModel viewModel = await Scanned(ops);

        viewModel.Access.PrimaryCommand.Execute(null);
        await WaitFor(() => viewModel.Dialog is SetupDialogViewModel);
        var dialog = Assert.IsType<SetupDialogViewModel>(viewModel.Dialog);

        Assert.Equal(FakeAdministratorAccess.Plan, dialog.Plan);
        Assert.Contains("/usr/libexec/pennyworth/pennyworth-helper", dialog.Reads);
        Assert.Contains("/usr/share/polkit-1/actions/app.pennyworth.policy", dialog.Reads);
        Assert.Contains("0123 4567 89ab cdef", dialog.Reads); // the fingerprints, grouped to compare by eye
        Assert.Contains("fedc ba98 7654 3210", dialog.Reads);
        Assert.Contains("MB", dialog.Reads);
        Assert.Contains("starts nothing", dialog.Wont);
        Assert.Equal(Strings.AdminSetupGo, dialog.AskText);
        Assert.False(dialog.NeedsSetup); // the setup does not offer itself
        Assert.Equal(0, ops.Access.SetupCalls);
    }

    [Fact]
    public async Task Setup_installs_then_offers_to_turn_access_on_with_its_own_prompt()
    {
        FakePrivileged ops = NeedsSetup();
        MainWindowViewModel viewModel = await Scanned(ops);
        await viewModel.RequestSetup();
        var dialog = Assert.IsType<SetupDialogViewModel>(viewModel.Dialog);

        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(1, ops.Access.SetupCalls);
        Assert.True(viewModel.Access.IsOff); // installed, not turned on: the setup starts nothing
        Assert.Equal(0, ops.Access.TurnOnCalls);
        Assert.IsType<AccessDialogViewModel>(viewModel.Dialog);
    }

    [Theory]
    [InlineData(PrivilegedOutcome.Cancelled, PrivilegedDialogState.Cancelled)]
    [InlineData(PrivilegedOutcome.Failed, PrivilegedDialogState.Failed)]
    [InlineData(PrivilegedOutcome.NotAvailable, PrivilegedDialogState.Unavailable)]
    public async Task A_setup_that_was_declined_or_failed_changes_nothing(PrivilegedOutcome outcome, PrivilegedDialogState state)
    {
        FakePrivileged ops = NeedsSetup();
        ops.Access.SetupResult = outcome switch
        {
            PrivilegedOutcome.Cancelled => PrivilegedResult<bool>.Cancelled,
            PrivilegedOutcome.NotAvailable => PrivilegedResult<bool>.NotAvailable(PrivilegedUnavailableReason.NoAuthenticationAgent),
            _ => PrivilegedResult<bool>.Failed("boom"),
        };
        MainWindowViewModel viewModel = await Scanned(ops);
        await viewModel.RequestSetup();
        var dialog = Assert.IsType<SetupDialogViewModel>(viewModel.Dialog);

        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(state, dialog.State);
        Assert.Equal(1, ops.Access.SetupCalls);
        Assert.True(viewModel.Access.NeedsSetup);
        Assert.Same(dialog, viewModel.Dialog);
        if (state == PrivilegedDialogState.Failed)
        {
            Assert.Equal(Strings.AdminSetupFailed, dialog.Message);
        }
    }

    [Fact]
    public async Task A_copy_of_the_app_with_nothing_to_install_says_so_instead_of_offering_a_button_that_does_nothing()
    {
        FakePrivileged ops = NeedsSetup();
        ops.Access.PlanResult = PrivilegedResult<AdministratorSetupPlan>.NotAvailable(PrivilegedUnavailableReason.SetupNotBundled);
        MainWindowViewModel viewModel = await Scanned(ops);

        await viewModel.RequestSetup();

        var dialog = Assert.IsType<SetupDialogViewModel>(viewModel.Dialog);
        Assert.Null(dialog.Plan);
        Assert.Equal(PrivilegedDialogState.Unavailable, dialog.State);
        Assert.Equal(Strings.PrivReasonSetupNotBundled, dialog.Message);
        Assert.False(dialog.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Files_that_cannot_be_read_are_a_failure_with_no_install()
    {
        FakePrivileged ops = NeedsSetup();
        ops.Access.PlanResult = PrivilegedResult<AdministratorSetupPlan>.Failed("link");
        MainWindowViewModel viewModel = await Scanned(ops);

        await viewModel.RequestSetup();

        var dialog = Assert.IsType<SetupDialogViewModel>(viewModel.Dialog);
        Assert.Equal(PrivilegedDialogState.Failed, dialog.State);
        Assert.Equal(0, ops.Access.SetupCalls);
    }

    [Fact]
    public void The_fingerprint_is_the_first_sixteen_digits_in_groups_of_four()
    {
        Assert.Equal("0123 4567 89ab cdef", SetupDialogViewModel.Fingerprint("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
    }

    // ---- bug 2: the button asks, and a missing helper is said and offered ----

    [Fact]
    public async Task Measuring_the_locked_folders_when_access_is_off_starts_the_one_prompt()
    {
        FakePrivileged ops = Ops();
        MainWindowViewModel viewModel = await Scanned(ops);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;

        summary.MeasureLockedCommand.Execute(null);
        var dialog = Assert.IsType<LockedMeasureDialogViewModel>(viewModel.Dialog);
        Assert.Empty(ops.FolderCalls); // the dialog explains first
        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal([true], ops.PromptFlags); // this request was allowed to ask, and did
        Assert.True(viewModel.Access.IsActive);
        Assert.Equal(0, summary.LockedCount);
    }

    [Fact]
    public async Task With_the_helper_missing_the_locked_folders_dialog_says_so_and_only_the_setup_moves_forward()
    {
        FakePrivileged ops = NeedsSetup();
        MainWindowViewModel viewModel = await Scanned(ops);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;

        summary.MeasureLockedCommand.Execute(null);
        var dialog = Assert.IsType<LockedMeasureDialogViewModel>(viewModel.Dialog);

        Assert.True(dialog.NeedsSetup);
        Assert.True(dialog.ShowSetupOffer);
        Assert.False(dialog.ShowAsk); // there is no one-off way to measure folders
        Assert.Equal(Strings.PrivNeedsSetup, dialog.SetupNote);
        Assert.Empty(ops.FolderCalls);
    }

    [Fact]
    public async Task The_setup_from_the_locked_folders_dialog_comes_back_to_it_ready_to_ask()
    {
        FakePrivileged ops = NeedsSetup();
        ops.FoldersResult = paths => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([new MeasuredFolder(paths[0], 10 * Gb, 10 * Gb, 42, true)]);
        MainWindowViewModel viewModel = await Scanned(ops);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;
        viewModel.RequestLockedMeasure(summary);
        var first = Assert.IsType<LockedMeasureDialogViewModel>(viewModel.Dialog);

        first.SetUpCommand.Execute(null);
        await first.Run;
        var setup = Assert.IsType<SetupDialogViewModel>(viewModel.Dialog);
        setup.StartCommand.Execute(null);
        await setup.Run;

        var again = Assert.IsType<LockedMeasureDialogViewModel>(viewModel.Dialog);
        Assert.False(again.NeedsSetup);
        Assert.True(again.ShowAsk);
        Assert.Equal(Strings.PrivAsk, again.AskText);
        Assert.Empty(ops.FolderCalls); // nothing was measured behind the person's back
        again.StartCommand.Execute(null);
        await again.Run;
        Assert.Equal(0, summary.LockedCount);
    }

    [Fact]
    public async Task The_space_dialog_with_the_helper_missing_offers_both_the_one_off_ask_and_the_setup()
    {
        FakePrivileged ops = NeedsSetup();
        MainWindowViewModel viewModel = await Scanned(ops);
        await Unlock(viewModel);

        viewModel.RequestAdminSpace();
        var dialog = Assert.IsType<AdminSpaceDialogViewModel>(viewModel.Dialog);

        Assert.True(dialog.ShowSetupOffer);
        Assert.True(dialog.ShowAsk);
        Assert.Equal(Strings.PrivAskOnce, dialog.AskText);
        Assert.Equal(Strings.PrivNeedsSetupOneOff, dialog.SetupNote);
        dialog.StartCommand.Execute(null);
        await dialog.Run;
        Assert.Single(ops.SpaceCalls);
        Assert.True(viewModel.Access.NeedsSetup); // the fake's one-off ask did not turn anything on: still needs setup
    }

    [Fact]
    public async Task A_message_about_a_helper_that_turned_out_old_offers_the_setup_too()
    {
        FakePrivileged ops = Ops();
        MainWindowViewModel viewModel = await Scanned(ops);
        viewModel.RequestLockedMeasure(viewModel.SystemSummary!);
        var dialog = Assert.IsType<LockedMeasureDialogViewModel>(viewModel.Dialog);
        ops.FoldersResult = _ => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.NotAvailable(PrivilegedUnavailableReason.HelperOutdated);
        ops.Access.State = AdministratorAccessState.NeedsSetup;
        ops.Access.Reason = PrivilegedUnavailableReason.HelperOutdated;

        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(PrivilegedDialogState.Unavailable, dialog.State);
        Assert.Equal(Strings.PrivReasonHelperOutdated, dialog.Message);
        Assert.True(dialog.OffersSetupAfterMessage);
    }

    // ---- what happens when access becomes active ----

    [Fact]
    public async Task Turning_access_on_measures_the_locked_folders_without_asking_again_and_the_totals_add_up()
    {
        FakePrivileged ops = Ops();
        MainWindowViewModel viewModel = await Scanned(ops);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;
        long unaccountedBefore = summary.Unaccounted.Bytes;
        Assert.Equal(1, summary.LockedCount);

        ops.Access.Set(AdministratorAccessState.Active);
        await viewModel.AccessWork;

        Assert.Equal([false], ops.PromptFlags); // measured inside the session; nothing may ask
        Assert.Equal(Secret, Assert.Single(ops.FolderCalls.Single()));
        Assert.Equal(0, summary.LockedCount);
        Assert.False(summary.ShowLockedBanner);
        Assert.Equal(unaccountedBefore - (10 * Gb), summary.Unaccounted.Bytes);
        Assert.Equal(1.0, Total(summary), 3);
        StorageNode secret = summary.Tree!.Root.GetChildren().Single(child => child.Name == "Secret");
        Assert.True(secret.IsLocked);
        Assert.True(secret.IsMeasured);
        Assert.Equal(10 * Gb, secret.SizeOnDisk);
    }

    [Fact]
    public async Task The_folders_measured_with_admin_rights_are_named_on_the_storage_page()
    {
        FakePrivileged ops = Ops();
        MainWindowViewModel viewModel = await Scanned(ops);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;
        Assert.False(summary.HasMeasuredByAdmin);

        ops.Access.Set(AdministratorAccessState.Active);
        await viewModel.AccessWork;

        Assert.True(summary.HasMeasuredByAdmin);
        Assert.Equal(string.Format(Strings.MeasuredByAdmin, Secret), summary.MeasuredByAdminText);
    }

    [Fact]
    public async Task A_folder_that_stayed_closed_even_to_the_administrator_is_said_to_have()
    {
        FakePrivileged ops = Ops();
        ops.FoldersResult = paths => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([new MeasuredFolder("/not/asked", 1, 1, 1, true)]);
        MainWindowViewModel viewModel = await Scanned(ops);

        ops.Access.Set(AdministratorAccessState.Active);
        await viewModel.AccessWork;

        StorageSummaryViewModel summary = viewModel.SystemSummary!;
        Assert.Equal(1, summary.LockedCount);
        Assert.False(summary.ShowLockedBanner); // nothing more to offer: it is on
        Assert.False(summary.HasMeasuredByAdmin);
        Assert.True(summary.ShowLockedLeft);
        Assert.Equal(string.Format(Strings.LockedLeftOne, 1), summary.LockedLeftText);
    }

    [Fact]
    public async Task A_measurement_that_fails_inside_the_session_is_said_and_can_be_tried_again()
    {
        FakePrivileged ops = Ops();
        ops.FoldersResult = _ => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Failed("boom");
        MainWindowViewModel viewModel = await Scanned(ops);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;

        ops.Access.Set(AdministratorAccessState.Active);
        await viewModel.AccessWork;

        Assert.True(summary.ShowLockedRetry);
        Assert.True(summary.ShowLockedBanner);
        Assert.Equal(string.Format(Strings.LockedRetryOne, 1), summary.LockedBannerText);
        Assert.Equal(Strings.PrivTryAgain, summary.LockedCta);

        ops.FoldersResult = paths => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([new MeasuredFolder(paths[0], 3 * Gb, 3 * Gb, 5, true)]);
        summary.MeasureLockedCommand.Execute(null);
        await viewModel.AccessWork;

        Assert.Equal(0, summary.LockedCount);
        Assert.False(summary.ShowLockedRetry);
        Assert.False(summary.ShowLockedBanner);
    }

    [Fact]
    public async Task A_scan_that_finishes_while_access_is_on_measures_its_locked_folders_at_once()
    {
        FakePrivileged ops = Ops(AdministratorAccessState.Active);

        MainWindowViewModel viewModel = await Scanned(ops);
        await viewModel.AccessWork;

        StorageSummaryViewModel summary = viewModel.SystemSummary!;
        Assert.Equal(0, summary.LockedCount);
        Assert.Equal([false], ops.PromptFlags);
        Assert.Equal(1.0, Total(summary), 3);
    }

    [Fact]
    public async Task With_access_on_the_measure_button_does_not_ask_or_open_a_dialog()
    {
        FakePrivileged ops = Ops();
        ops.FoldersResult = paths => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([]);
        MainWindowViewModel viewModel = await Scanned(ops);
        ops.Access.Set(AdministratorAccessState.Active);
        await viewModel.AccessWork;
        ops.FoldersResult = paths => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([new MeasuredFolder(paths[0], 5 * Gb, 5 * Gb, 1, true)]);

        viewModel.RequestLockedMeasure(viewModel.SystemSummary!);
        await viewModel.AccessWork;

        Assert.Null(viewModel.Dialog);
        Assert.NotNull(viewModel.Toast);
        Assert.All(ops.PromptFlags, flag => Assert.False(flag));
    }

    [Fact]
    public async Task Access_turning_on_includes_the_administrator_only_space_when_the_system_space_is_unlocked()
    {
        FakePrivileged ops = Ops();
        MainWindowViewModel viewModel = await Scanned(ops);
        await Unlock(viewModel);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;
        Assert.False(summary.IncludesAdminSpace);

        ops.Access.Set(AdministratorAccessState.Active);
        await viewModel.AccessWork;

        Assert.True(summary.IncludesAdminSpace);
        Assert.False(summary.ShowAdminSpaceBanner);
        Assert.Equal(TestApp.DriveC.RootPath, Assert.Single(ops.SpaceCalls));
        Assert.Equal(1.0, Total(summary), 3);
    }

    [Fact]
    public async Task Unlocking_the_system_space_while_access_is_on_includes_the_administrator_parts_in_one_go()
    {
        FakePrivileged ops = Ops(AdministratorAccessState.Active);
        MainWindowViewModel viewModel = await Scanned(ops);
        await viewModel.AccessWork;

        await Unlock(viewModel);

        Assert.True(viewModel.SystemSummary!.IncludesAdminSpace);
        Assert.Single(ops.SpaceCalls);
        Assert.All(ops.PromptFlags, flag => Assert.False(flag));
    }

    [Fact]
    public async Task The_space_button_with_access_on_just_includes_it()
    {
        FakePrivileged ops = Ops();
        MainWindowViewModel viewModel = await Scanned(ops);
        await Unlock(viewModel);
        ops.Access.Set(AdministratorAccessState.Active);
        await viewModel.AccessWork;
        ops.SpaceCalls.Clear();

        viewModel.RequestAdminSpace();
        await viewModel.AccessWork;

        Assert.Null(viewModel.Dialog);
    }

    [Fact]
    public async Task What_started_the_session_and_what_the_session_announces_are_not_measured_twice()
    {
        FakePrivileged ops = Ops();
        MainWindowViewModel viewModel = await Scanned(ops);
        viewModel.RequestLockedMeasure(viewModel.SystemSummary!);
        var dialog = Assert.IsType<LockedMeasureDialogViewModel>(viewModel.Dialog);

        dialog.StartCommand.Execute(null);
        await dialog.Run;
        await viewModel.AccessWork;

        Assert.Single(ops.FolderCalls);
    }

    // ---- the memory modules ----

    private static PcSpecs ModulesUnknown() => new SamplePcReader().Read() with { Memory = new MemorySpecs(16 * Gb, null, []) };

    private static readonly IReadOnlyList<MemoryModule> TwoModules =
    [
        new("DIMM_A1", 8L << 30, "DDR4", 3200, 3000, "Kingston", "KF3200C16D4/8GX", "DIMM"),
        new("DIMM_B1", 8L << 30, "DDR4", 3200, 3000, "Kingston", "KF3200C16D4/8GX", "DIMM"),
    ];

    private static async Task<(MainWindowViewModel ViewModel, FakeModulesReader Reader, FakePrivileged Ops)> SpecsApp(AdministratorAccessState state, MemoryModulesResult? answer = null)
    {
        FakePrivileged ops = Ops(state);
        var reader = new FakeModulesReader(answer ?? MemoryModulesResult.Read(TwoModules, 4));
        var services = TestApp.Services(scanner: new FakeScanner(_ => TreeWithLockedFolder()), privileged: ops) with
        {
            Specs = new FixedPc(ModulesUnknown()),
            MemoryModules = reader,
        };
        var viewModel = new MainWindowViewModel(services);
        viewModel.Initialize();
        await viewModel.CurrentScan;
        await viewModel.Specs.EnsureLoaded();
        return (viewModel, reader, ops);
    }

    [Fact]
    public async Task The_modules_fill_in_when_access_turns_on_without_a_prompt()
    {
        var (viewModel, reader, ops) = await SpecsApp(AdministratorAccessState.Off);
        SpecsViewModel specs = viewModel.Specs;
        Assert.True(specs.CanReadModules);

        ops.Access.Set(AdministratorAccessState.Active);
        await viewModel.AccessWork;

        Assert.Equal([false], reader.PromptFlags);
        Assert.False(specs.CanReadModules);
        Assert.Equal(2, specs.Specs!.Memory.Modules.Count);
        Assert.False(specs.IsReadingModules);
        Assert.Null(specs.ModulesMessage);
    }

    [Fact]
    public async Task Opening_the_specs_while_access_is_on_reads_the_modules_without_a_prompt()
    {
        var (viewModel, reader, _) = await SpecsApp(AdministratorAccessState.Active);

        Assert.Equal([false], reader.PromptFlags);
        Assert.Equal(2, viewModel.Specs.Specs!.Memory.Modules.Count);
    }

    [Fact]
    public async Task The_modules_button_may_ask_and_says_what_it_will_do_in_each_state()
    {
        var (viewModel, reader, ops) = await SpecsApp(AdministratorAccessState.Off);
        SpecsViewModel specs = viewModel.Specs;
        Assert.Equal(Strings.SpecsModulesAsksOff, specs.ModulesAsksText);

        ops.Access.Set(AdministratorAccessState.NeedsSetup, PrivilegedUnavailableReason.HelperNotInstalled);
        Assert.Equal(Strings.SpecsModulesAsksNeedsSetup, specs.ModulesAsksText);
        Assert.True(specs.ShowModulesSetup);

        ops.Access.Set(AdministratorAccessState.Off);
        await specs.ReadModulesCommand.ExecuteAsync(null);

        Assert.Equal([true], reader.PromptFlags);
    }

    [Fact]
    public async Task A_read_in_the_running_session_that_fails_says_so_instead_of_leaving_the_button_silent()
    {
        var (viewModel, _, ops) = await SpecsApp(AdministratorAccessState.Off, MemoryModulesResult.Unavailable);

        ops.Access.Set(AdministratorAccessState.Active);
        await viewModel.AccessWork;

        Assert.Equal(Strings.SpecsModulesUnavailable, viewModel.Specs.ModulesMessage);
        Assert.True(viewModel.Specs.CanReadModules);
    }

    [Fact]
    public async Task A_read_that_did_not_turn_access_on_says_it_was_a_one_off()
    {
        var (viewModel, _, ops) = await SpecsApp(AdministratorAccessState.NeedsSetup);
        SpecsViewModel specs = viewModel.Specs;
        Assert.False(specs.ShowModulesOneOff);

        await specs.ReadModulesCommand.ExecuteAsync(null);

        Assert.True(specs.ShowModulesOneOff);
        Assert.Equal(AdministratorAccessState.NeedsSetup, ops.Access.State);
        Assert.True(specs.ShowModulesSetup);
    }

    [Fact]
    public async Task A_read_inside_an_active_session_is_not_a_one_off()
    {
        var (viewModel, _, _) = await SpecsApp(AdministratorAccessState.Active);

        Assert.False(viewModel.Specs.ShowModulesOneOff);
    }

    [Fact]
    public async Task The_setup_button_of_the_specs_opens_the_setup()
    {
        var (viewModel, _, ops) = await SpecsApp(AdministratorAccessState.NeedsSetup, MemoryModulesResult.Cancelled);

        viewModel.Specs.SetUpCommand.Execute(null);
        await WaitFor(() => viewModel.Dialog is SetupDialogViewModel);

        Assert.IsType<SetupDialogViewModel>(viewModel.Dialog);
        Assert.Equal(0, ops.Access.SetupCalls);
    }

    // ---- the root-owned cleanup rules ----

    [Fact]
    public async Task A_root_rule_that_needs_the_setup_offers_it_and_becomes_selectable_when_the_helper_is_there()
    {
        FakePrivileged ops = NeedsSetup();
        var service = new AdminFakeCleanup([(PackagesRule, 4 * Gb, PrivilegedCapability.Missing(PrivilegedUnavailableReason.HelperNotInstalled))]);
        var services = TestApp.Services(cleanup: service, privileged: ops);
        var viewModel = new MainWindowViewModel(services);
        viewModel.Initialize();
        await viewModel.Cleanup.MeasureAsync();
        CleanupItemViewModel packages = Assert.Single(viewModel.Cleanup.Items);
        Assert.False(packages.IsAvailable);
        Assert.True(packages.OffersSetup);
        Assert.Equal(PrivilegedText.Reason(PrivilegedUnavailableReason.HelperNotInstalled), packages.AdminReasonText);

        service.Capability = PrivilegedCapability.Ready; // the helper was installed
        ops.Access.Set(AdministratorAccessState.Off);
        await viewModel.Cleanup.CurrentMeasure;

        CleanupItemViewModel now = Assert.Single(viewModel.Cleanup.Items);
        Assert.True(now.IsAvailable);
        Assert.True(now.NeedsPassword);
        Assert.False(now.OffersSetup);
        Assert.False(now.IsSelected); // a rule that deletes as root is never pre-ticked
        viewModel.Cleanup.ToggleCommand.Execute(now);
        Assert.True(now.IsSelected);
    }

    [Fact]
    public async Task A_root_rule_says_no_password_is_needed_while_access_is_on_and_the_confirmation_agrees()
    {
        FakePrivileged ops = Ops();
        var service = new AdminFakeCleanup([(PackagesRule, 4 * Gb, PrivilegedCapability.Ready)]);
        var viewModel = new MainWindowViewModel(TestApp.Services(cleanup: service, privileged: ops));
        viewModel.Initialize();
        await viewModel.Cleanup.MeasureAsync();
        CleanupItemViewModel packages = Assert.Single(viewModel.Cleanup.Items);
        viewModel.Cleanup.ToggleCommand.Execute(packages);
        Assert.True(packages.NeedsPassword);
        Assert.Contains("password", new CleanConfirmDialogViewModel(viewModel.Cleanup, viewModel).PasswordNote);

        ops.Access.Set(AdministratorAccessState.Active);

        Assert.False(packages.NeedsPassword);
        Assert.True(packages.AccessIsOn);
        var confirm = new CleanConfirmDialogViewModel(viewModel.Cleanup, viewModel);
        Assert.True(confirm.ShowPasswordNote);
        Assert.Equal(string.Format(Strings.ConfirmAccessOnNote, packages.Title), confirm.PasswordNote);
    }

    // ---- words ----

    [Fact]
    public void Every_reason_including_the_new_ones_has_its_own_sentence()
    {
        foreach (PrivilegedUnavailableReason reason in Enum.GetValues<PrivilegedUnavailableReason>())
        {
            Assert.NotEqual(Strings.PrivReasonUnknown, PrivilegedText.Reason(reason));
        }
    }

    // ---- restricted folder rows and the unassigned bucket ----

    [Fact]
    public async Task Activating_a_locked_folder_in_the_browser_when_off_turns_access_on_and_updates_it_in_place()
    {
        FakePrivileged ops = Ops();
        MainWindowViewModel viewModel = await Scanned(ops);
        viewModel.ShowFoldersCommand.Execute(null);

        FolderItemViewModel secret = viewModel.Folders.Items.Single(i => i.Name == "Secret");
        Assert.True(secret.ShowMeasureAffordance);
        Assert.Equal(Strings.LockedRowTip, secret.RowTip);
        Assert.Equal("—", secret.SizeText);

        viewModel.Folders.OpenCommand.Execute(secret);
        var dialog = Assert.IsType<AccessDialogViewModel>(viewModel.Dialog);
        Assert.Equal(@"C:\", viewModel.Folders.Current?.FullPath); // stays on the list instead of entering a dead screen

        dialog.StartCommand.Execute(null);
        await dialog.Run;
        await viewModel.AccessWork;

        FolderItemViewModel updated = viewModel.Folders.Items.Single(i => i.Name == "Secret");
        Assert.True(updated.IsMeasured);
        Assert.False(updated.ShowMeasureAffordance);
        Assert.Equal(ByteSize.Format(10 * Gb), updated.SizeText);
        Assert.Equal(string.Format(Strings.LockedFolderMeasuredToast, Secret, ByteSize.Format(10 * Gb)), viewModel.Toast);
    }

    [Fact]
    public async Task Activating_a_locked_folder_when_needs_setup_offers_setup_then_turns_on_and_cancelling_says_so()
    {
        FakePrivileged ops = NeedsSetup();
        ops.FoldersResult = paths => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([new MeasuredFolder(paths[0], 4 * Gb, 4 * Gb, 9, true)]);
        MainWindowViewModel viewModel = await Scanned(ops);
        viewModel.ShowFoldersCommand.Execute(null);

        viewModel.Folders.OpenCommand.Execute(viewModel.Folders.Items.Single(i => i.Name == "Secret"));
        await viewModel.AccessWork;
        var setup = Assert.IsType<SetupDialogViewModel>(viewModel.Dialog);

        setup.StartCommand.Execute(null);
        await setup.Run;
        var turnOn = Assert.IsType<AccessDialogViewModel>(viewModel.Dialog);

        ops.Access.TurnOnResult = PrivilegedResult<bool>.Cancelled;
        turnOn.StartCommand.Execute(null);
        await turnOn.Run;
        Assert.Equal(PrivilegedDialogState.Cancelled, turnOn.State);
        Assert.Equal(Strings.PrivCancelled, turnOn.Message);

        ops.Access.TurnOnResult = PrivilegedResult<bool>.Done(true);
        turnOn.TryAgainCommand.Execute(null);
        turnOn.StartCommand.Execute(null);
        await turnOn.Run;
        await viewModel.AccessWork;

        Assert.True(viewModel.Folders.Items.Single(i => i.Name == "Secret").IsMeasured);
    }

    [Fact]
    public async Task Activating_a_locked_folder_while_active_measures_just_that_folder_with_no_prompt()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        int first = b.AddDirectory(StorageTreeBuilder.RootIndex, "RootA", StorageCategory.Other);
        b.MarkLocked(first);
        int second = b.AddDirectory(StorageTreeBuilder.RootIndex, "RootB", StorageCategory.Other);
        b.MarkLocked(second);

        FakePrivileged ops = Ops(AdministratorAccessState.Off);
        MainWindowViewModel viewModel = await Scanned(ops, tree: _ => b.Build());
        ops.FoldersResult = _ => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([]); // initial activation finds nothing yet
        ops.Access.Set(AdministratorAccessState.Active);
        await viewModel.AccessWork;
        ops.FolderCalls.Clear();
        ops.PromptFlags.Clear();

        string pathA = Path.Combine(@"C:\", "RootA");
        ops.FoldersResult = _ => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([new MeasuredFolder(pathA, 2 * Gb, 2 * Gb, 3, true)]);
        viewModel.ShowFoldersCommand.Execute(null);
        viewModel.Folders.OpenCommand.Execute(viewModel.Folders.Items.Single(i => i.Name == "RootA"));
        await viewModel.AccessWork;

        Assert.Null(viewModel.Dialog);
        Assert.Equal([false], ops.PromptFlags);
        Assert.Equal([pathA], Assert.Single(ops.FolderCalls));
        Assert.True(viewModel.Folders.Items.Single(i => i.Name == "RootA").IsMeasured);
        Assert.False(viewModel.Folders.Items.Single(i => i.Name == "RootB").IsMeasured);
        Assert.Equal(string.Format(Strings.LockedFolderMeasuredToast, pathA, ByteSize.Format(2 * Gb)), viewModel.Toast);
    }

    [Fact]
    public async Task Activating_the_unassigned_row_attributes_both_locked_folders_and_system_space()
    {
        FakePrivileged ops = Ops();
        ops.SpaceResult = PrivilegedResult<HiddenSpaceReport>.Done(new HiddenSpaceReport(6 * Gb, 2 * Gb));
        MainWindowViewModel viewModel = await Scanned(ops);
        await Unlock(viewModel); // now the row is "Unaccounted" / "Não atribuído"
        StorageSummaryViewModel summary = viewModel.SystemSummary!;

        Assert.True(summary.OffersAdminAttribution);
        Assert.Equal(Strings.UnassignedAction, summary.Unaccounted.ActionText);
        Assert.Equal(Strings.CategoryHint_UnaccountedAdmin, summary.Unaccounted.Hint);
        Assert.Equal(Strings.UnassignedExplain, summary.UnaccountedExplanation);
        Assert.Contains(summary.LockedFolderRows, r => r.Path == Secret && !r.IsMeasured);

        summary.ActivateCategory(summary.Unaccounted);
        var dialog = Assert.IsType<AccessDialogViewModel>(viewModel.Dialog);
        dialog.StartCommand.Execute(null);
        await dialog.Run;
        await viewModel.AccessWork;

        Assert.False(summary.OffersAdminAttribution);
        Assert.Null(summary.Unaccounted.ActionText);
        Assert.Equal(Strings.CategoryHint_UnaccountedMeasured, summary.Unaccounted.Hint);
        Assert.Equal(Strings.UnassignedNothingLeft, summary.UnaccountedExplanation);
        Assert.True(summary.ShowAttributedByAdmin);
        Assert.Contains(ByteSize.Format(18 * Gb), summary.AttributedByAdminText); // 10 GB folder + 8 GB system space

        LockedFolderRowViewModel measuredRow = Assert.Single(summary.LockedFolderRows);
        Assert.True(measuredRow.IsMeasured);
        summary.ActivateLockedFolderCommand.Execute(measuredRow);
        Assert.Equal(Page.Folders, viewModel.Page);
        Assert.True(viewModel.Folders.IsMeasuredLockedFolder);
    }

    [Fact]
    public async Task Activating_the_unassigned_row_when_needs_setup_or_active_uses_the_unified_flow()
    {
        FakePrivileged needs = NeedsSetup();
        MainWindowViewModel first = await Scanned(needs);
        first.SystemSummary!.ActivateCategory(first.SystemSummary.Unaccounted);
        await first.AccessWork;
        Assert.IsType<SetupDialogViewModel>(first.Dialog);

        FakePrivileged ops = Ops();
        ops.FoldersResult = _ => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([]);
        MainWindowViewModel active = await Scanned(ops);
        ops.Access.Set(AdministratorAccessState.Active);
        await active.AccessWork;
        ops.FoldersResult = paths => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([new MeasuredFolder(paths[0], 7 * Gb, 7 * Gb, 2, true)]);
        ops.SpaceResult = PrivilegedResult<HiddenSpaceReport>.Done(new HiddenSpaceReport(3 * Gb, 1 * Gb));
        ops.PromptFlags.Clear();

        active.SystemSummary!.MeasureUnassignedCommand.Execute(null);
        await active.AccessWork;

        Assert.Null(active.Dialog);
        Assert.All(ops.PromptFlags, flag => Assert.False(flag));
        Assert.Equal(string.Format(Strings.UnassignedMeasured, ByteSize.Format(11 * Gb)), active.Toast);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FixedPc(PcSpecs specs) : ISpecsReader
    {
        public PcSpecs Read() => specs;
    }

    private sealed class FakeModulesReader(MemoryModulesResult answer) : IMemoryModuleReader
    {
        public List<bool> PromptFlags { get; } = [];

        public Task<MemoryModulesResult> ReadAsync(CancellationToken cancellationToken, bool promptIfNeeded = true)
        {
            PromptFlags.Add(promptIfNeeded);
            return Task.FromResult(answer);
        }
    }
}
