namespace Pennyworth.Assistant.Tests;

public class ActionLinkerTests
{
    private static readonly AssistantAction Cleanup = new(ActionKind.Cleanup);
    private static readonly AssistantAction Specs = new(ActionKind.Specs);

    private static (string Text, List<AssistantAction> Actions) Run(params string[] chunks)
    {
        var linker = new ActionLinker([Cleanup, Specs]);
        var updates = chunks.SelectMany(linker.Push).ToList();
        updates.AddRange(linker.Flush());
        return (string.Concat(updates.OfType<TextUpdate>().Select(u => u.Text)), [.. updates.OfType<ActionUpdate>().Select(u => u.Action)]);
    }

    [Fact]
    public void Turns_a_code_into_an_action_and_removes_it_from_the_text()
    {
        var (text, actions) = Run("Shall I tidy up? [[action:cleanup]]");

        Assert.Equal("Shall I tidy up? ", text);
        Assert.Equal([Cleanup], actions);
    }

    [Fact]
    public void Handles_a_code_split_across_chunks()
    {
        var (text, actions) = Run("Here: [", "[act", "ion:spe", "cs]", "] done");

        Assert.Equal("Here:  done", text);
        Assert.Equal([Specs], actions);
    }

    [Theory]
    [InlineData("See [[action:specs]]]]")]
    [InlineData("See [[action:specs]].].")]
    [InlineData("See [action:specs]")]
    [InlineData("See [[action:specs]")]
    public void Tolerates_the_bracket_mistakes_small_models_make(string answer)
    {
        var (text, actions) = Run(answer);

        Assert.Equal("See ", text);
        Assert.Equal([Specs], actions);
    }

    [Fact]
    public void Drops_actions_that_are_not_allowed_and_keeps_only_the_first()
    {
        var (text, actions) = Run("A [[action:memory]] B [[action:cleanup]] C [[action:specs]]");

        Assert.Equal("A  B  C ", text);
        Assert.Equal([Cleanup], actions);
    }

    [Fact]
    public void Leaves_ordinary_brackets_alone()
    {
        var (text, actions) = Run("Arrays look like [1, 2] and [[maybe]] too.");

        Assert.Equal("Arrays look like [1, 2] and [[maybe]] too.", text);
        Assert.Empty(actions);
    }

    [Fact]
    public void Releases_an_unfinished_code_as_text_at_the_end()
    {
        var (text, actions) = Run("Almost [[action:spe");

        Assert.Equal("Almost [[action:spe", text);
        Assert.Empty(actions);
    }

    [Fact]
    public void Text_after_a_code_keeps_flowing()
    {
        var (text, _) = Run("[[action:cleanup]]", "]", " Then rest.");

        Assert.Equal(" Then rest.", text);
    }
}
