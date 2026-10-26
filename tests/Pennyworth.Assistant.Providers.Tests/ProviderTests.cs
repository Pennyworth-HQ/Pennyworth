using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Pennyworth.Assistant.Providers.Tests;

public class ProviderTests
{
    private static async Task<(string Text, long? Output)> Stream(IChatClient client)
    {
        string text = "";
        long? output = null;
        List<ChatMessage> messages = [new(ChatRole.System, "Be brief."), new(ChatRole.User, "Hi")];
        await foreach (var update in client.GetStreamingResponseAsync(messages, new ChatOptions { MaxOutputTokens = 50 }, TestContext.Current.CancellationToken))
        {
            text += update.Text;
            output = update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details.OutputTokenCount ?? output;
        }

        return (text, output);
    }

    [Theory]
    [InlineData(AssistantBackend.OpenAiCompatible, null, "https://api.openai.com/v1/chat/completions")]
    [InlineData(AssistantBackend.OpenAiCompatible, "http://localhost:1234/v1", "http://localhost:1234/v1/chat/completions")]
    [InlineData(AssistantBackend.Gemini, null, "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    [InlineData(AssistantBackend.AzureOpenAi, "https://contoso.openai.azure.com", "https://contoso.openai.azure.com/openai/v1/chat/completions")]
    public async Task OpenAi_family_streams_through_the_right_endpoint(AssistantBackend backend, string? endpoint, string expectedUrl)
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse(Wire.OpenAiStream));
        var client = ChatClientFactory.Create(new AssistantProfile("p", backend, "Mine", "the-model", endpoint), "sk-secret", handler.Client());

        var (text, output) = await Stream(client);

        Assert.Equal("Hello there", text);
        Assert.Equal(2, output);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(expectedUrl, request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-secret", request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("the-model", json.RootElement.GetProperty("model").GetString());
        Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Anthropic_sends_the_messages_api_shape_and_streams()
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse(Wire.AnthropicStream));
        var client = ChatClientFactory.Create(new AssistantProfile("p", AssistantBackend.Anthropic, "Claude", "claude-x"), "ak-secret", handler.Client());

        var (text, output) = await Stream(client);

        Assert.Equal("Hello there", text);
        Assert.Equal(2, output);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
        Assert.Equal("ak-secret", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicChatClient.ApiVersion, request.Headers.GetValues("anthropic-version").Single());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Be brief.", json.RootElement.GetProperty("system").GetString());
        Assert.Equal(50, json.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("user", json.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Anthropic_answers_once_with_usage_and_merges_consecutive_turns()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(Wire.AnthropicSingle));
        var client = new AnthropicChatClient(handler.Client(), "k", "claude-x");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "a"), new ChatMessage(ChatRole.User, "b"), new ChatMessage(ChatRole.Assistant, "c")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("OK", response.Text);
        Assert.Equal(1, response.Usage!.OutputTokenCount);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        using var json = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("a\n\nb", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(json.RootElement.TryGetProperty("system", out _));
    }

    [Theory]
    [InlineData(AssistantBackend.Anthropic, HttpStatusCode.Unauthorized, AssistantErrorKind.BadKey)]
    [InlineData(AssistantBackend.OpenAiCompatible, HttpStatusCode.Unauthorized, AssistantErrorKind.BadKey)]
    [InlineData(AssistantBackend.OpenAiCompatible, HttpStatusCode.NotFound, AssistantErrorKind.UnknownModel)]
    [InlineData(AssistantBackend.Anthropic, HttpStatusCode.TooManyRequests, AssistantErrorKind.Quota)]
    public async Task Test_connection_explains_refusals(AssistantBackend backend, HttpStatusCode status, AssistantErrorKind expected)
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"error":{"message":"nope","type":"x"}}""", status));
        var client = ChatClientFactory.Create(new AssistantProfile("p", backend, "x", "m"), "k", handler.Client());

        var result = await ConnectionTester.TestAsync(client, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(expected, result.Error!.Kind);
        Assert.DoesNotContain("\"k\"", result.Error.Detail);
        Assert.Single(handler.Requests); // refusals are not retried
    }

    [Fact]
    public async Task Test_connection_reports_the_reply()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(Wire.OpenAiSingle));
        var client = ChatClientFactory.Create(new AssistantProfile("p", AssistantBackend.OpenAiCompatible, "x", "m"), "k", handler.Client());

        var result = await ConnectionTester.TestAsync(client, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal("OK", result.Reply);
    }

    [Fact]
    public async Task Retries_a_server_error_once_before_any_text()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"error":{"message":"busy"}}""", HttpStatusCode.ServiceUnavailable), _ => FakeHandler.Sse(Wire.AnthropicStream));
        var client = ChatClientFactory.Create(new AssistantProfile("p", AssistantBackend.Anthropic, "x", "m"), "k", handler.Client());

        var (text, _) = await Stream(client);

        Assert.Equal("Hello there", text);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Does_not_repeat_an_answer_that_already_started()
    {
        const string broken = "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Half\"}}\n\n" +
                              "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";
        var handler = new FakeHandler(_ => FakeHandler.Sse(broken), _ => FakeHandler.Sse(Wire.AnthropicStream));
        var client = ChatClientFactory.Create(new AssistantProfile("p", AssistantBackend.Anthropic, "x", "m"), "k", handler.Client());

        var error = await Assert.ThrowsAsync<ProviderException>(() => Stream(client));

        Assert.Equal("Overloaded", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_slow_provider_times_out()
    {
        var client = new ResilientChatClient(new AnthropicChatClient(new HttpClient(new SlowHandler()), "k", "m"), TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(AssistantErrorKind.Timeout, AssistantError.From(error, TestContext.Current.CancellationToken).Kind);
    }

    [Fact]
    public void Azure_endpoint_accepts_both_forms()
    {
        Assert.Equal("https://r.openai.azure.com/openai/v1/", ChatClientFactory.AzureEndpoint("https://r.openai.azure.com/").ToString());
        Assert.Equal("https://r.openai.azure.com/openai/v1/", ChatClientFactory.AzureEndpoint("https://r.openai.azure.com/openai/v1").ToString());
        Assert.Throws<ArgumentException>(() => ChatClientFactory.AzureEndpoint(" "));
        Assert.Throws<ArgumentException>(() => ChatClientFactory.Create(new AssistantProfile("l", AssistantBackend.Local, "x", "m"), ""));
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
