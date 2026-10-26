using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Pennyworth.Assistant.Providers;

/// <summary>
/// Builds the chat client for a cloud profile. OpenAI-compatible endpoints, Gemini and Azure all
/// go through the OpenAI SDK (AOT-clean); Anthropic uses <see cref="AnthropicChatClient"/>.
/// Every client is wrapped in <see cref="ResilientChatClient"/>.
/// </summary>
public static class ChatClientFactory
{
    public static readonly Uri OpenAiEndpoint = new("https://api.openai.com/v1/");

    /// <summary>Gemini's OpenAI-compatible endpoint.</summary>
    public static readonly Uri GeminiEndpoint = new("https://generativelanguage.googleapis.com/v1beta/openai/");

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <param name="http">Optional transport (tests, proxies); one shared client is used otherwise.</param>
    public static IChatClient Create(AssistantProfile profile, string apiKey, HttpClient? http = null)
    {
        http ??= SharedHttp;
        IChatClient inner = profile.Backend switch
        {
            AssistantBackend.OpenAiCompatible => OpenAi(EndpointOrDefault(profile.Endpoint, OpenAiEndpoint), apiKey, profile.ModelId, http),
            AssistantBackend.Gemini => OpenAi(EndpointOrDefault(profile.Endpoint, GeminiEndpoint), apiKey, profile.ModelId, http),
            AssistantBackend.AzureOpenAi => OpenAi(AzureEndpoint(profile.Endpoint), apiKey, profile.ModelId, http),
            AssistantBackend.Anthropic => new AnthropicChatClient(http, apiKey, profile.ModelId, profile.Endpoint is { Length: > 0 } e ? new Uri(WithSlash(e)) : null),
            _ => throw new ArgumentException("Local profiles are created by Pennyworth.Assistant.Local.", nameof(profile)),
        };
        return new ResilientChatClient(inner, RequestTimeout);
    }

    /// <summary>"https://res.openai.azure.com" → "https://res.openai.azure.com/openai/v1/" (the v1 API takes the deployment as the model).</summary>
    public static Uri AzureEndpoint(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new ArgumentException("Azure OpenAI needs the resource endpoint.", nameof(resource));
        }

        string trimmed = resource.Trim().TrimEnd('/');
        return new Uri(trimmed.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase) ? trimmed + "/" : trimmed + "/openai/v1/");
    }

    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static IChatClient OpenAi(Uri endpoint, string apiKey, string model, HttpClient http)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0), // ResilientChatClient retries once, before any text arrives
            NetworkTimeout = RequestTimeout,
        };
        return new OpenAIClient(new ApiKeyCredential(apiKey), options).GetChatClient(model).AsIChatClient();
    }

    private static Uri EndpointOrDefault(string? endpoint, Uri fallback) =>
        string.IsNullOrWhiteSpace(endpoint) ? fallback : new Uri(WithSlash(endpoint.Trim()));

    private static string WithSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
