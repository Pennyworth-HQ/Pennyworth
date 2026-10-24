namespace Pennyworth.Scanning.Tests;

public sealed class HiddenSpaceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pw-hidden-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("C:", true)]
    [InlineData("d:", true)]
    [InlineData(@"C:\", false)]
    [InlineData("C: & calc", false)]
    [InlineData("", false)]
    public void Accepts_only_plain_drive_letters(string drive, bool expected)
    {
        Assert.Equal(expected, HiddenSpace.IsDriveLetter(drive));
    }

    [Fact]
    public void Rejects_anything_else_before_running()
    {
        Assert.Throws<ArgumentException>(() => HiddenSpace.Measure("C:' ; Remove-Item x"));
    }

    [Fact]
    public void Reports_round_trip_through_the_result_file()
    {
        string path = Path.Combine(_dir, "result.json");
        var report = new HiddenSpaceReport(RestorePoints: 12_000, NtfsMetadata: null);

        HiddenSpace.WriteReport(report, path);

        Assert.Equal(report, HiddenSpace.ReadReport(path));
        Assert.Equal(12_000, report.Total);
        Assert.Null(HiddenSpace.ReadReport(Path.Combine(_dir, "missing.json")));
    }

    [Fact]
    public void Volume_metadata_degrades_to_null_without_admin()
    {
        long? mft = HiddenSpace.MeasureNtfsMetadata("C:");

        // Elevated test runs see a real size; normal runs must not throw.
        Assert.True(mft is null or > 0);
    }
}
