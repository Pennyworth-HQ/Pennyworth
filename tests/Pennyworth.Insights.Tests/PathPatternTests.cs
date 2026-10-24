using Pennyworth.Insights.Classification;

namespace Pennyworth.Insights.Tests;

public class PathPatternTests
{
    [Theory]
    [InlineData(@"C:\Windows", @"C:\Windows", true)]
    [InlineData(@"C:\Windows", @"c:\WINDOWS", true)]
    [InlineData(@"C:\Windows", @"C:\Windows\System32", false)]
    [InlineData(@"C:\Users\*", @"C:\Users\Bob", true)]
    [InlineData(@"C:\Users\*", @"C:\Users", false)]
    [InlineData(@"?:\pagefile.sys", @"D:\pagefile.sys", true)]
    [InlineData(@"?:\pagefile.sys", @"D:\Games\pagefile.sys", false)]
    [InlineData(@"**\steamapps", @"D:\SteamLibrary\steamapps", true)]
    [InlineData(@"**\steamapps", @"C:\Program Files (x86)\Steam\steamapps", true)]
    [InlineData(@"**\steamapps", @"C:\steamapps\common", false)]
    public void Matches_paths(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathPattern.Parse(pattern).IsMatch(PathPattern.SplitPath(path)));
    }

    [Fact]
    public void Exact_patterns_are_more_specific_than_wildcard_prefixes()
    {
        Assert.True(PathPattern.Parse(@"C:\Users\*\AppData").Specificity > PathPattern.Parse(@"C:\Users\*").Specificity);
        Assert.True(PathPattern.Parse(@"C:\steamapps").Specificity > PathPattern.Parse(@"**\steamapps").Specificity);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"**")]
    [InlineData(@"C:\**\x")]
    public void Rejects_invalid_patterns(string pattern)
    {
        Assert.Throws<FormatException>(() => PathPattern.Parse(pattern));
    }
}
