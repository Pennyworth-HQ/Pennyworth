using Pennyworth.Core.Storage;
using Pennyworth.Insights.Classification;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Insights.Tests;

public class RuleClassifierTests
{
    private static readonly Dictionary<string, string> Environment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["SystemDrive"] = "C:",
        ["ProgramData"] = @"C:\ProgramData",
    };

    private static RuleClassifier CreateWithEmbeddedRules() =>
        new(KnowledgeBaseLoader.LoadEmbedded().Locations, name => Environment.GetValueOrDefault(name));

    [Theory]
    [InlineData(@"C:\Windows", StorageCategory.Windows)]
    [InlineData(@"C:\Program Files", StorageCategory.Apps)]
    [InlineData(@"D:\Program Files (x86)", StorageCategory.Apps)]
    [InlineData(@"C:\Users\Bob", StorageCategory.YourFiles)]
    [InlineData(@"C:\Users\Bob\AppData", StorageCategory.Apps)]
    [InlineData(@"D:\SteamLibrary\steamapps", StorageCategory.Games)]
    [InlineData(@"C:\Program Files\Epic Games", StorageCategory.Games)]
    [InlineData(@"C:\hiberfil.sys", StorageCategory.HiddenSystem)]
    [InlineData(@"E:\pagefile.sys", StorageCategory.HiddenSystem)]
    [InlineData(@"C:\System Volume Information", StorageCategory.HiddenSystem)]
    [InlineData(@"C:\$Recycle.Bin", StorageCategory.YourFiles)]
    public void Classifies_known_locations(string path, StorageCategory expected)
    {
        Assert.Equal(expected, CreateWithEmbeddedRules().Classify(path, StorageCategory.Other));
    }

    [Fact]
    public void Unknown_paths_keep_the_inherited_category()
    {
        var classifier = CreateWithEmbeddedRules();

        Assert.Equal(StorageCategory.Windows, classifier.Classify(@"C:\Windows\System32", StorageCategory.Windows));
        Assert.Equal(StorageCategory.Other, classifier.Classify(@"D:\Stuff", StorageCategory.Other));
    }

    [Fact]
    public void Finds_the_location_for_friendly_titles()
    {
        var location = CreateWithEmbeddedRules().FindLocation(@"C:\Users\Bob\AppData");

        Assert.Equal("user-appdata", location?.Id);
    }

    [Fact]
    public void Skips_rules_whose_environment_variable_is_missing()
    {
        var classifier = new RuleClassifier(KnowledgeBaseLoader.LoadEmbedded().Locations, _ => null);

        Assert.Equal(StorageCategory.Other, classifier.Classify(@"C:\Windows", StorageCategory.Other));
        Assert.Equal(StorageCategory.Apps, classifier.Classify(@"C:\Program Files", StorageCategory.Other));
    }

    [Theory]
    [InlineData(@"%SystemRoot%\Temp", @"C:\Windows\Temp")]
    [InlineData(@"%SystemDrive%\Users\*", @"C:\Users\*")]
    [InlineData(@"?:\pagefile.sys", @"?:\pagefile.sys")]
    public void Expands_environment_variables(string path, string expected)
    {
        Assert.Equal(expected, RuleClassifier.ExpandVariables(path, name => Environment.GetValueOrDefault(name)));
    }
}
