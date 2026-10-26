using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Pennyworth.Assistant.Providers;

/// <summary>
/// A small client for Anthropic's Messages API: text in, text out, streaming and usage. The
/// official SDK can't be used because it fails under Native AOT (spec §17).
/// </summary>
public sealed class AnthropicChatClient(HttpClient http, string apiKey, string model, Uri? baseUrl = null) : IChatClient
{
    public const string ApiVersion = "2023-06-01";
    public static readonly Uri DefaultBaseUrl = new("https://api.anthropic.com/");

    private readonly Uri _messages = new(baseUrl ?? DefaultBaseUrl, "v1/messages");

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Build(messages, options, stream: false);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await ThrowIfFailed(response, cancellationToken);

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var text = new StringBuilder();
        foreach (JsonElement block in json.RootElement.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text")
            {
                text.Append(block.GetProperty("text").GetString());
            }
        }

        var result = new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString()))
        {
            ModelId = json.RootElement.TryGetProperty("model", out var m) ? m.GetString() : model,
            FinishReason = FinishReason(json.RootElement.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null),
        };
        if (json.RootElement.TryGetProperty("usage", out var usage))
        {
            result.Usage = new UsageDetails { InputTokenCount = Number(usage, "input_tokens"), OutputTokenCount = Number(usage, "output_tokens") };
        }

        return result;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Build(messages, options, stream: true);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowIfFailed(response, cancellationToken);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        long? input = null, output = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            using JsonDocument json = JsonDocument.Parse(line.AsMemory(5).Trim());
            JsonElement e = json.RootElement;
            switch (e.GetProperty("type").GetString())
            {
                case "message_start" when e.GetProperty("message").TryGetProperty("usage", out var usage):
                    input = Number(usage, "input_tokens");
                    output = Number(usage, "output_tokens");
                    break;
                case "content_block_delta" when e.GetProperty("delta") is var delta && delta.GetProperty("type").GetString() == "text_delta":
                    yield return new ChatResponseUpdate(ChatRole.Assistant, delta.GetProperty("text").GetString());
                    break;
                case "message_delta":
                    if (e.TryGetProperty("usage", out var final))
                    {
                        output = Number(final, "output_tokens") ?? output;
                    }

                    break;
                case "error":
                    throw new ProviderException(500, e.GetProperty("error").GetProperty("message").GetString() ?? "stream error");
                case "message_stop":
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output })],
                    };
                    yield break;
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("anthropic", _messages, model) : null;

    public void Dispose()
    {
    }

    private HttpRequestMessage Build(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var system = new List<string>();
        var turns = new List<AnthropicMessage>();
        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                system.Add(message.Text);
                continue;
            }

            string role = message.Role == ChatRole.Assistant ? "assistant" : "user";
            if (turns.Count > 0 && turns[^1].Role == role)
            {
                turns[^1] = turns[^1] with { Content = turns[^1].Content + "\n\n" + message.Text }; // the API wants alternating turns
            }
            else
            {
                turns.Add(new AnthropicMessage(role, message.Text));
            }
        }

        var body = new AnthropicRequest(
            options?.ModelId ?? model,
            options?.MaxOutputTokens ?? 1024,
            system.Count == 0 ? null : string.Join("\n\n", system),
            turns,
            options?.Temperature,
            stream);

        var request = new HttpRequestMessage(HttpMethod.Post, _messages)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, AnthropicJsonContext.Default.AnthropicRequest), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        return request;
    }

    private static async Task ThrowIfFailed(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string message = response.ReasonPhrase ?? "error";
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var text))
            {
                message = text.GetString() ?? message;
            }
        }
        catch (JsonException)
        {
        }

        throw new ProviderException((int)response.StatusCode, message);
    }

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static ChatFinishReason? FinishReason(string? stop) => stop switch
    {
        "end_turn" or "stop_sequence" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        "refusal" => ChatFinishReason.ContentFilter,
        null => null,
        _ => new ChatFinishReason(stop),
    };
}

internal sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record AnthropicRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string? System,
    [property: JsonPropertyName("messages")] IReadOnlyList<AnthropicMessage> Messages,
    [property: JsonPropertyName("temperature")] float? Temperature,
    [property: JsonPropertyName("stream")] bool Stream);

[JsonSerializable(typeof(AnthropicRequest))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class AnthropicJsonContext : JsonSerializerContext;
