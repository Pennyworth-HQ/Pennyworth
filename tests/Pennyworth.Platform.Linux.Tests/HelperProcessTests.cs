using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Pennyworth.Core.Privilege;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>
/// The real <c>pennyworth-helper serve</c> binary (built with these tests) run as the ordinary user over real pipes through the
/// app's own <see cref="PkexecChannel"/> and <see cref="ElevationSession"/>: what a person's machine does when the helper is not
/// started by root. The read-only operations work on what the user can read and match <c>du</c>; the root-only ones fail with their
/// documented outcome; the session ends when its input does. No root, no pkexec, no password.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class HelperProcessTests : IDisposable
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private readonly TempTree _tree = new();
    private readonly List<ISessionChannel> _channels = [];

    public void Dispose()
    {
        foreach (ISessionChannel channel in _channels)
        {
            channel.Dispose();
        }

        _tree.Dispose();
    }

    /// <summary>The helper the build produced next to the tests: same configuration, <c>src/Pennyworth.Helper/bin/&lt;config&gt;/net10.0/pennyworth-helper</c>.</summary>
    private static string HelperBinary()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/')).Parent!.Name;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pennyworth.slnx")))
            {
                string path = Path.Combine(dir.FullName, "src", "Pennyworth.Helper", "bin", configuration, "net10.0", "pennyworth-helper");
                return File.Exists(path) ? path : throw new FileNotFoundException("The helper was not built: " + path);
            }
        }

        throw new InvalidOperationException("Pennyworth.slnx was not found.");
    }

    /// <summary>The apphost needs to find the runtime the tests run on.</summary>
    private static string DotnetRoot() =>
        Environment.GetEnvironmentVariable("DOTNET_ROOT")
        ?? Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

    private ISessionChannel Launch()
    {
        ISessionChannel channel = PkexecChannel.Launch("/bin/sh", ["-c", $"DOTNET_ROOT='{DotnetRoot()}' exec '{HelperBinary()}' serve"])
            ?? throw new InvalidOperationException("The helper did not start.");
        _channels.Add(channel);
        return channel;
    }

    private LinuxPrivilegedOperations Ops(out List<ISessionChannel> started)
    {
        var channels = new List<ISessionChannel>();
        started = channels;
        return Privileged.Ops(
            new FakeRunner(),
            Privileged.TrustedLstat,
            timeout: TimeSpan.FromSeconds(60),
            launch: (_, _) =>
            {
                ISessionChannel channel = Launch();
                channels.Add(channel);
                return channel;
            });
    }

    private static ServeRequest Request(string op, string? argument = null, IReadOnlyList<string>? paths = null) =>
        new(HelperProtocol.Version, 1, op, argument, paths);

    private static async Task<ServeResponse> Ask(ISessionChannel channel, ServeRequest request)
    {
        await ServeFrames.WriteAsync(channel.Input, JsonSerializer.SerializeToUtf8Bytes(request, HelperJson.Default.ServeRequest), Token);
        (FrameStatus status, byte[] payload) = await ServeFrames.ReadAsync(channel.Output, HelperProtocol.MaxResponseBytes, Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
        Assert.Equal(FrameStatus.Frame, status);
        return JsonSerializer.Deserialize(payload, HelperJson.Default.ServeResponse)!;
    }

    private static string Du(string path)
    {
        var start = new ProcessStartInfo("/usr/bin/du", ["-s", "-x", "--block-size=1", path]) { RedirectStandardOutput = true, UseShellExecute = false };
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\t')[0];
    }

    [LinuxFact]
    public async Task The_real_helper_serves_hello_and_exits_with_success_when_its_input_closes()
    {
        ISessionChannel channel = Launch();

        ServeResponse hello = await Ask(channel, Request(HelperProtocol.Hello));
        channel.Input.Close();

        Assert.Equal(new ServeResponse(HelperProtocol.Version, 1, HelperExit.Ok), hello);
        Assert.Equal(HelperExit.Ok, await channel.Exited.WaitAsync(TimeSpan.FromSeconds(30), Token));
    }

    [LinuxFact]
    public async Task Measuring_a_readable_folder_matches_du_through_the_real_helper()
    {
        _tree.Write("data/a.bin", 10_000);
        _tree.Write("data/sub/b.bin", 70_000);
        _tree.Write("data/sub/deeper/c.bin", 1);
        Directory.CreateDirectory(_tree.PathOf("data/empty"));
        string folder = _tree.PathOf("data");
        ISessionChannel channel = Launch();

        ServeResponse response = await Ask(channel, Request(HelperProtocol.MeasureFolders, paths: [folder, "/nonexistent-folder-xyz", "relative"]));

        Assert.Equal(HelperExit.Ok, response.Exit);
        FolderSize measured = Assert.Single(response.Folders!.Folders);
        Assert.Equal(folder, measured.Path);
        Assert.Equal(long.Parse(Du(folder), System.Globalization.CultureInfo.InvariantCulture), measured.SizeOnDisk);
        Assert.Equal(80_001, measured.LogicalSize);
        Assert.Equal(3, measured.Files);
        Assert.True(measured.Complete);
    }

    [LinuxFact]
    public async Task The_whole_chain_app_session_real_helper_measures_folders_and_asks_once()
    {
        _tree.Write("data/a.bin", 5_000);
        string folder = _tree.PathOf("data");
        LinuxPrivilegedOperations ops = Ops(out List<ISessionChannel> started);

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> first = await ops.MeasureFoldersAsync([folder], Token);
        PrivilegedResult<IReadOnlyList<MeasuredFolder>> second = await ops.MeasureFoldersAsync([folder], Token);

        Assert.Equal(PrivilegedOutcome.Done, first.Outcome);
        Assert.Equal(MeasuredSize(first), MeasuredSize(second));
        Assert.Equal(5_000, first.Value![0].LogicalSize);
        Assert.Single(started);
        Assert.Equal(AdministratorAccessState.Active, ops.Access.State);

        ops.Access.Lock();
        Assert.Equal(HelperExit.Ok, await started[0].Exited.WaitAsync(TimeSpan.FromSeconds(30), Token)); // the real helper left on end of input
    }

    private static long MeasuredSize(PrivilegedResult<IReadOnlyList<MeasuredFolder>> result) => result.Value![0].SizeOnDisk;

    [LinuxFact]
    public async Task What_only_root_may_do_fails_with_its_documented_outcome_and_the_session_survives()
    {
        ISessionChannel channel = Launch();

        ServeResponse clean = await Ask(channel, Request(HelperProtocol.CleanSystem, "system-crash-reports") with { Files = [] });
        ServeResponse unknown = await Ask(channel, Request("read-file", "/etc/shadow"));
        ServeResponse firmware = await Ask(channel, Request(HelperProtocol.ReadFirmwareTables));
        ServeResponse hello = await Ask(channel, Request(HelperProtocol.Hello));

        Assert.Equal((HelperExit.Refused, "cleaning system folders needs administrator rights"), (clean.Exit, clean.Error));
        Assert.Equal((HelperExit.Usage, "unknown operation"), (unknown.Exit, unknown.Error));
        // /sys/firmware/dmi/tables/DMI is readable only by root; where it is not there at all it is missing. Either way: no table, no crash.
        Assert.Contains(firmware.Exit, new[] { HelperExit.Ok, HelperExit.Missing, HelperExit.Refused, HelperExit.Failed });
        Assert.Equal(HelperExit.Ok, hello.Exit);
    }

    [LinuxFact]
    public async Task A_frame_over_the_limit_ends_the_real_helper_with_its_documented_code()
    {
        ISessionChannel channel = Launch();
        await channel.Input.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, Token);
        await channel.Input.FlushAsync(Token);

        (FrameStatus status, byte[] payload) = await ServeFrames.ReadAsync(channel.Output, HelperProtocol.MaxResponseBytes, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(FrameStatus.Frame, status);
        Assert.Equal(HelperExit.TooLarge, JsonSerializer.Deserialize(payload, HelperJson.Default.ServeResponse)!.Exit);
        Assert.Equal(HelperExit.TooLarge, await channel.Exited.WaitAsync(TimeSpan.FromSeconds(30), Token));
    }

    [LinuxFact]
    public async Task The_real_helper_never_writes_anything_but_frames_to_standard_output_and_one_line_to_standard_error()
    {
        ISessionChannel channel = Launch();
        await channel.Input.WriteAsync(new byte[] { 0, 0, 0, 3, (byte)'n', (byte)'o', (byte)'t' }, Token);
        await channel.Input.FlushAsync(Token);

        (FrameStatus status, byte[] payload) = await ServeFrames.ReadAsync(channel.Output, HelperProtocol.MaxResponseBytes, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(FrameStatus.Frame, status);
        Assert.Equal(HelperExit.Usage, JsonSerializer.Deserialize(payload, HelperJson.Default.ServeResponse)!.Exit);
        channel.Input.Close();
        Assert.Equal(HelperExit.Ok, await channel.Exited.WaitAsync(TimeSpan.FromSeconds(30), Token));
        Assert.Equal(FrameStatus.EndOfStream, (await ServeFrames.ReadAsync(channel.Output, 1024, Token)).Status);
    }
}
