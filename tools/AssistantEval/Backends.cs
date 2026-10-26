using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;
using OpenAI;

namespace AssistantEval;

public static class Backends
{
    /// <summary>
    /// <c>onnx</c>: a local ONNX GenAI model folder, on DirectML (<c>dml</c>) or CPU (<c>cpu</c>).
    /// <c>openai</c>: any OpenAI-compatible endpoint (OpenAI, Azure v1, Gemini, OpenRouter, Ollama, LM Studio).
    /// </summary>
    public static IChatClient Create(Options options) => options.Backend switch
    {
        "onnx" => Onnx(options.Model, options.Ep, options.ProviderOptions),
        "openai" => new OpenAIClient(
                new ApiKeyCredential(Environment.GetEnvironmentVariable(options.KeyEnv ?? "") ?? "none"),
                new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint ?? "https://api.openai.com/v1") })
            .GetChatClient(options.Model).AsIChatClient(),
        _ => throw new ArgumentException($"Unknown backend '{options.Backend}'"),
    };

    private static IChatClient Onnx(string modelPath, string ep, IReadOnlyList<(string Key, string Value)> providerOptions)
    {
        var config = new Config(modelPath);
        config.ClearProviders();
        if (ep != "cpu")
        {
            config.AppendProvider(ep);
            foreach (var (key, value) in providerOptions)
            {
                config.SetProviderOption(ep, key, value);
            }
        }

        var model = new Model(config);
        var tokenizer = new Tokenizer(model);
        var clientOptions = new OnnxRuntimeGenAIChatClientOptions
        {
            // The model's own chat template (tokenizer_config.json), so each model gets its native format.
            PromptFormatter = (messages, _) => tokenizer.ApplyChatTemplate(null, ToJson(messages), null, true),
            StopSequences = ["<|end|>", "<|endoftext|>", "<end_of_turn>", "<|im_end|>"],
        };
        return new OnnxRuntimeGenAIChatClient(model, true, clientOptions);
    }

    private static string ToJson(IEnumerable<ChatMessage> messages)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (ChatMessage message in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role.Value);
                writer.WriteString("content", message.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
