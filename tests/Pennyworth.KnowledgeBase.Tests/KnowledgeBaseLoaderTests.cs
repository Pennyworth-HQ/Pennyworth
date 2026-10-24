using System.Globalization;
using Pennyworth.Core.Storage;

namespace Pennyworth.KnowledgeBase.Tests;

public class KnowledgeBaseLoaderTests
{
    [Fact]
    public void Parses_locations_with_both_languages()
    {
        const string json = """
            {
              "version": 1,
              "locations": [
                {
                  "id": "windows",
                  "path": "%SystemRoot%",
                  "category": "Windows",
                  "title": { "en": "Windows", "pt-BR": "Janelas" },
                  "description": { "en": "System", "pt-BR": "Sistema" }
                }
              ]
            }
            """;

        var data = KnowledgeBaseLoader.Parse(json);

        var location = Assert.Single(data.Locations);
        Assert.Equal("windows", location.Id);
        Assert.Equal(StorageCategory.Windows, location.Category);
        Assert.Equal("Windows", location.Title.Get(CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("Janelas", location.Title.Get(CultureInfo.GetCultureInfo("pt-BR")));
    }

    [Fact]
    public void Embedded_knowledge_base_is_complete_and_unique()
    {
        var data = KnowledgeBaseLoader.LoadEmbedded();

        Assert.NotEmpty(data.Locations);
        Assert.Equal(data.Locations.Count, data.Locations.Select(l => l.Id).Distinct().Count());
        Assert.All(data.Locations, location =>
        {
            Assert.False(string.IsNullOrWhiteSpace(location.Path));
            Assert.False(string.IsNullOrWhiteSpace(location.Title.En));
            Assert.False(string.IsNullOrWhiteSpace(location.Title.PtBr));
            Assert.False(string.IsNullOrWhiteSpace(location.Description.En));
            Assert.False(string.IsNullOrWhiteSpace(location.Description.PtBr));
        });
    }
}

public class CatalogTests
{
    [Fact]
    public void Process_explanations_are_complete_and_unique()
    {
        var processes = KnowledgeBaseLoader.LoadProcesses().Processes;

        Assert.NotEmpty(processes);
        Assert.Equal(processes.Count, processes.Select(p => p.Id).Distinct().Count());
        var names = processes.SelectMany(p => p.Names).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(processes, p =>
        {
            Assert.NotEmpty(p.Names);
            Assert.False(string.IsNullOrWhiteSpace(p.Title.PtBr));
            Assert.False(p.IsProtected && p.CanClose, $"{p.Id} is protected but closable");
        });
    }

    [Fact]
    public void Cleanup_rules_are_complete_and_unique()
    {
        var rules = KnowledgeBaseLoader.LoadCleanupRules().Rules;

        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.Contains(rules, r => r.Kind == CleanupKind.RecycleBin);
        Assert.All(rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Consequence.PtBr));
            Assert.True(r.Kind == CleanupKind.RecycleBin || r.Paths.Count > 0, $"{r.Id} has no paths");
        });
    }
}
