using Microsoft.Extensions.AI;

namespace Pennyworth.Assistant.Tests;

public class ProfileTests
{
    [Theory]
    [InlineData(AssistantBackend.Local, true)]
    [InlineData(AssistantBackend.OpenAiCompatible, false)]
    [InlineData(AssistantBackend.Anthropic, false)]
    public void Only_local_profiles_skip_the_send_preview(AssistantBackend backend, bool isLocal)
    {
        Assert.Equal(isLocal, new AssistantProfile("p1", backend, "Mine", "model").IsLocal);
    }

    [Fact]
    public async Task Fake_client_streams_its_script_and_records_the_call()
    {
        var client = new FakeChatClient(["Hello ", "there"]);
        var text = "";
        long? output = null;

        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
        {
            text += update.Text;
            output = update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details.OutputTokenCount ?? output;
        }

        Assert.Equal("Hello there", text);
        Assert.Equal(2, output);
        Assert.Equal("hi", Assert.Single(Assert.Single(client.Calls)).Text);
    }
}
