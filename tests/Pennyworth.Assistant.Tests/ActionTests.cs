namespace Pennyworth.Assistant.Tests;

public class ActionTests
{
    [Theory]
    [InlineData("cleanup", ActionKind.Cleanup, null)]
    [InlineData("cleanup.item:temp", ActionKind.CleanupItem, "temp")]
    [InlineData(" Close-App:chrome ", ActionKind.CloseApp, "chrome")]
    [InlineData(@"folders:C:\Users\ana", ActionKind.Folders, @"C:\Users\ana")]
    [InlineData("specs", ActionKind.Specs, null)]
    public void Parses_known_codes(string code, ActionKind kind, string? argument)
    {
        Assert.Equal(new AssistantAction(kind, argument), AssistantAction.Parse(code));
    }

    [Theory]
    [InlineData("format-disk")]
    [InlineData("cleanup.item")]
    [InlineData("cleanup.item:")]
    [InlineData("memory:chrome")]
    [InlineData("")]
    public void Rejects_unknown_or_malformed_codes(string code)
    {
        Assert.Null(AssistantAction.Parse(code));
    }

    [Fact]
    public void Writes_the_token_the_model_uses()
    {
        Assert.Equal("[[action:cleanup.item:temp]]", new AssistantAction(ActionKind.CleanupItem, "temp").Token);
        Assert.Equal("[[action:memory]]", new AssistantAction(ActionKind.Memory).Token);
    }
}
