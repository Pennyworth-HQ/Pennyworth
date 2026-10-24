using System.Globalization;
using Pennyworth.Core.Formatting;

namespace Pennyworth.Core.Tests;

public class ByteSizeTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(10L * 1024 * 1024 + 512 * 1024, "10.5 MB")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 GB")]
    [InlineData(-5L, "0 B")]
    public void Formats_with_binary_units(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes, En));
    }

    [Fact]
    public void Uses_culture_decimal_separator()
    {
        Assert.Equal("1,50 KB", ByteSize.Format(1536, CultureInfo.GetCultureInfo("pt-BR")));
    }
}
