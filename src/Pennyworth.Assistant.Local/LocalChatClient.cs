using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace Pennyworth.Assistant.Local;

/// <summary>
/// A downloaded model as an <see cref="IChatClient"/>. The model loads on the first question,
/// answers one question at a time on a background thread (greedy decoding, optional repetition
/// penalty), stops at the model's end markers, and unloads after
/// <see cref="IdleUnload"/> so the app returns to its normal memory use (spec §10).
/// </summary>
public sealed class LocalChatClient : IChatClient
{
    /// <summary>
    /// Off by default: AssistantEval showed 1.05–1.1 makes the model avoid the facts' own numbers
    /// (spec §17). Loops are stopped by the session's repetition guard instead.
    /// </summary>
    public const float DefaultRepetitionPenalty = 1.0f;

    private static readonly Lock HandleLock = new();
    private static OgaHandle? _handle;

    private readonly string _folder;
    private readonly LocalModel _model;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idle;
    private Model? _loaded;
    private Tokenizer? _tokenizer;
    private bool _disposed;

    public LocalChatClient(string folder, LocalModel model, TimeSpan? idleUnload = null, float repetitionPenalty = DefaultRepetitionPenalty)
    {
        _folder = folder;
        _model = model;
        RepetitionPenalty = repetitionPenalty;
        IdleUnload = idleUnload ?? TimeSpan.FromMinutes(5);
        _idle = new Timer(_ => _ = UnloadWhenIdleAsync());
    }

    public TimeSpan IdleUnload { get; }

    /// <summary>1 turns it off. It also weighs on the prompt's tokens, so too high a value makes the model avoid the facts' numbers.</summary>
    public float RepetitionPenalty { get; }

    public bool IsLoaded => _loaded is not null;

    /// <summary>Raised on the generating thread whenever the model is loaded or unloaded.</summary>
    public event EventHandler? LoadedChanged;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        UsageDetails? usage = null;
        await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            text.Append(update.Text);
            usage = update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details ?? usage;
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString())) { Usage = usage, ModelId = _model.Id };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        _idle.Change(Timeout.Infinite, Timeout.Infinite);
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        long input = 0, output = 0;
        Task producer = Task.CompletedTask;
        try
        {
            await Task.Run(EnsureLoaded, cancellationToken);
            string prompt = _tokenizer!.ApplyChatTemplate(null, ToJson(messages), null, true);
            int maxOutput = options?.MaxOutputTokens ?? 512;
            producer = Task.Run(() => Generate(prompt, maxOutput, channel.Writer, ref input, ref output, cancellationToken), CancellationToken.None);

            await foreach (string piece in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, piece) { ModelId = _model.Id };
            }

            await producer; // surfaces a generation error
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output })],
            };
        }
        finally
        {
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch
            {
                // Already surfaced above, or the person pressed Stop.
            }

            if (!_disposed)
            {
                _idle.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            }

            _gate.Release();
        }
    }

    /// <summary>Frees the model now (leaving the Ask screen, or after the idle time).</summary>
    public async Task UnloadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Unload();
        }
        finally
        {
            _gate.Release();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("onnxruntime-genai", null, _model.Id) : null;

    public void Dispose()
    {
        _disposed = true;
        _idle.Dispose();
        _gate.Wait();
        try
        {
            Unload();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Generate(string prompt, int maxOutput, ChannelWriter<string> writer, ref long input, ref long output, CancellationToken cancellationToken)
    {
        try
        {
            using Sequences tokens = _tokenizer!.Encode(prompt);
            input = tokens[0].Length;
            using var parameters = new GeneratorParams(_loaded!);
            parameters.SetSearchOption("max_length", input + maxOutput);
            parameters.SetSearchOption("do_sample", false);
            if (RepetitionPenalty != 1.0f)
            {
                parameters.SetSearchOption("repetition_penalty", RepetitionPenalty);
            }
            using var generator = new Generator(_loaded!, parameters);
            generator.AppendTokenSequences(tokens);
            using TokenizerStream stream = _tokenizer.CreateStream();
            var tail = new StringBuilder();
            int longestStop = _model.StopStrings.Count == 0 ? 0 : _model.StopStrings.Max(s => s.Length);

            while (!generator.IsDone())
            {
                cancellationToken.ThrowIfCancellationRequested();
                generator.GenerateNextToken();
                output++;
                tail.Append(stream.Decode(generator.GetNextTokens()[0]));

                // Hold back what could be the start of a stop marker; release the rest.
                string text = tail.ToString();
                if (_model.StopStrings.FirstOrDefault(s => text.Contains(s, StringComparison.Ordinal)) is { } stop)
                {
                    string before = text[..text.IndexOf(stop, StringComparison.Ordinal)];
                    if (before.Length > 0)
                    {
                        writer.TryWrite(before);
                    }

                    tail.Clear();
                    break;
                }

                int keep = Math.Min(longestStop - 1, text.Length);
                if (text.Length > keep)
                {
                    writer.TryWrite(text[..^keep]);
                    tail.Remove(0, text.Length - keep);
                }
            }

            if (tail.Length > 0)
            {
                writer.TryWrite(tail.ToString());
            }

            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            throw;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded is not null)
        {
            return;
        }

        lock (HandleLock)
        {
            _handle ??= new OgaHandle(); // native runtime shutdown at process exit
        }

        using var config = new Config(_folder);
        config.ClearProviders();
        if (_model.Device == ModelDevice.Gpu)
        {
            config.AppendProvider("dml");
        }

        _loaded = new Model(config);
        _tokenizer = new Tokenizer(_loaded);
        LoadedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Unload()
    {
        if (_loaded is null)
        {
            return;
        }

        _tokenizer?.Dispose();
        _loaded.Dispose();
        _tokenizer = null;
        _loaded = null;
        LoadedChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task UnloadWhenIdleAsync()
    {
        if (!_disposed && await _gate.WaitAsync(0))
        {
            try
            {
                Unload();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private static string ToJson(IEnumerable<ChatMessage> messages)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
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

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
