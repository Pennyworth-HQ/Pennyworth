using System.Diagnostics;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public class ProcessProbeTests
{
    [LinuxFact]
    public void Returns_what_the_program_printed()
    {
        Assert.Equal("hello", ProcessProbe.Run("/bin/echo", ["hello"], TimeSpan.FromSeconds(10))?.Trim());
    }

    [LinuxFact]
    public void Fixes_the_locale_so_output_parses_the_same_everywhere()
    {
        Assert.Equal("C", ProcessProbe.Run("/bin/sh", ["-c", "printf %s \"$LC_ALL\""], TimeSpan.FromSeconds(10)));
    }

    [LinuxFact]
    public void A_failing_program_is_null()
    {
        Assert.Null(ProcessProbe.Run("/bin/false", [], TimeSpan.FromSeconds(10)));
    }

    [LinuxFact]
    public void A_program_that_is_not_installed_is_null()
    {
        Assert.Null(ProcessProbe.Run("/nonexistent/pennyworth-probe", [], TimeSpan.FromSeconds(10)));
    }

    [LinuxFact]
    public void A_program_that_never_exits_is_killed_at_the_timeout()
    {
        var clock = Stopwatch.StartNew();

        string? output = ProcessProbe.Run("/bin/sleep", ["30"], TimeSpan.FromMilliseconds(300));

        Assert.Null(output);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed}");
    }

    [LinuxFact]
    public void A_program_that_waits_for_input_does_not_hang()
    {
        Assert.Equal("", ProcessProbe.Run("/bin/cat", [], TimeSpan.FromSeconds(10)));
    }
}
