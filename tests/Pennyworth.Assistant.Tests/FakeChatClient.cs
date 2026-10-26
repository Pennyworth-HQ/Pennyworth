using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Pennyworth.Assistant.Tests;

/// <summary>A model that answers from a script: one list of chunks per call, then usage.</summary>
internal sealed class FakeChatClient(params string[][] scripts) : IChatClient
{
    private int _call;

    /// <summary>The messages of every call, in order.</summary>
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    /// <summary>When set, the stream waits on this before every chunk after the first.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public long InputTokens { get; init; } = 120;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        string[] script = Next(messages, options);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(script)))
        {
            Usage = new UsageDetails { InputTokenCount = InputTokens, OutputTokenCount = script.Length },
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string[] script = Next(messages, options);
        for (int i = 0; i < script.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i > 0 && Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, script[i]);
        }

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new UsageContent(new UsageDetails { InputTokenCount = InputTokens, OutputTokenCount = script.Length })],
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private string[] Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        Calls.Add([.. messages]);
        Options.Add(options);
        return scripts[Math.Min(_call++, scripts.Length - 1)];
    }
}
