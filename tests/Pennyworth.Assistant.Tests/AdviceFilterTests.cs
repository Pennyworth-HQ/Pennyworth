using static Pennyworth.Assistant.Tests.Samples;

namespace Pennyworth.Assistant.Tests;

public class AdviceFilterTests
{
    private static (string Shown, int Blocked) Run(System.Globalization.CultureInfo culture, params string[] chunks)
    {
        var filter = AdviceFilter.FromKnowledgeBase(culture);
        var updates = chunks.SelectMany(filter.Push).ToList();
        updates.AddRange(filter.Flush());
        string shown = string.Concat(updates.Select(u => u switch
        {
            TextUpdate t => t.Text,
            BlockedUpdate b => "<blocked>" + b.Replacement,
            _ => "",
        }));
        return (shown, filter.BlockedCount);
    }

    [Theory]
    [InlineData("Open regedit and change the key.")]
    [InlineData("You could disable Windows Defender for a while.")]
    [InlineData("Turn off Windows Update to save data.")]
    [InlineData("Try overclocking the processor a little.")]
    [InlineData("Go into the BIOS and enable XMP.")]
    [InlineData("A RAM cleaner like this one helps.")]
    [InlineData("Run this command in PowerShell as admin.")]
    [InlineData("Delete the files in System32 you don't need.")]
    [InlineData("Open services.msc and stop the indexing service.")]
    public void Blocks_unsafe_advice_in_english(string sentence)
    {
        Assert.True(AdviceFilter.FromKnowledgeBase(En).IsBlocked(sentence));
    }

    [Theory]
    [InlineData("Desative o Windows Defender por um tempo.")]
    [InlineData("Você pode desativar as atualizações do Windows.")]
    [InlineData("Entre na BIOS e ative o XMP.")]
    [InlineData("Use um otimizador para acelerar o PC.")]
    [InlineData("Abra o prompt de comando e digite isto.")]
    [InlineData("Edite o registro para acelerar a inicialização.")]
    [InlineData("Apague a pasta do Windows antiga.")]
    public void Blocks_unsafe_advice_in_portuguese(string sentence)
    {
        Assert.True(AdviceFilter.FromKnowledgeBase(Pt).IsBlocked(sentence));
    }

    [Theory]
    [InlineData("I wouldn't disable Windows Security; it protects you.")]
    [InlineData("Please don't turn off Windows Update.")]
    [InlineData("Não desative o Windows Defender, ele protege o PC.")]
    [InlineData("Windows Update is installing, so the disk is busy.")]
    [InlineData("Your BIOS version is 5.17.")]
    [InlineData("The graphics card has 4 GB of video memory.")]
    [InlineData("Windows Security is scanning; the scan ends on its own.")]
    public void Lets_refusals_and_plain_facts_through(string sentence)
    {
        Assert.False(AdviceFilter.FromKnowledgeBase(En).IsBlocked(sentence));
    }

    [Fact]
    public void Replaces_only_the_offending_sentence_even_when_split_across_chunks()
    {
        var (shown, blocked) = Run(En, "Your drive is 92% full. You could dis", "able Windows Def", "ender. Cleanup can free 18 GB.");

        Assert.Equal(1, blocked);
        Assert.StartsWith("Your drive is 92% full. <blocked>That is not something I would recommend", shown);
        Assert.EndsWith("Cleanup can free 18 GB.", shown);
    }

    [Fact]
    public void Decimals_do_not_end_a_sentence()
    {
        var filter = AdviceFilter.FromKnowledgeBase(En);

        Assert.Empty(filter.Push("It holds 1.5"));
        Assert.Equal("It holds 1.5 GB. ", Assert.IsType<TextUpdate>(Assert.Single(filter.Push(" GB. Next"))).Text);
        Assert.Equal("Next", Assert.IsType<TextUpdate>(Assert.Single(filter.Flush())).Text);
    }

    [Fact]
    public void Embedded_patterns_compile()
    {
        var catalog = Pennyworth.KnowledgeBase.KnowledgeBaseLoader.LoadBlockedAdvice();

        Assert.NotEmpty(catalog.Topics);
        Assert.False(string.IsNullOrWhiteSpace(catalog.Replacement.PtBr));
        Assert.All(catalog.Topics.SelectMany(t => t.Patterns), p => _ = new System.Text.RegularExpressions.Regex(p));
    }
}
