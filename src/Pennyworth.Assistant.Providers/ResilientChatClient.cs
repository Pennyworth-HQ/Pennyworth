using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Pennyworth.Assistant.Providers;

/// <summary>
/// A time limit for each request, and one retry for transient failures (network, timeout, 5xx)
/// as long as no text has reached the person yet — a half-shown answer is never repeated.
/// </summary>
public sealed class ResilientChatClient(IChatClient inner, TimeSpan timeout) : DelegatingChatClient(inner)
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var limit = Limit(cancellationToken);
            try
            {
                return await base.GetResponseAsync(messages, options, limit.Token);
            }
            catch (Exception ex) when (attempt == 0 && ShouldRetry(ex, cancellationToken))
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No answer within {timeout.TotalSeconds:0} s.");
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var limit = Limit(cancellationToken);
            bool started = false;
            Exception? retry = null;
            await using var stream = base.GetStreamingResponseAsync(messages, options, limit.Token).GetAsyncEnumerator(limit.Token);
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await stream.MoveNextAsync())
                    {
                        yield break;
                    }

                    update = stream.Current;
                }
                catch (Exception ex) when (!started && attempt == 0 && ShouldRetry(ex, cancellationToken))
                {
                    retry = ex;
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"No answer within {timeout.TotalSeconds:0} s.");
                }

                started |= update.Text.Length > 0;
                yield return update;
            }

            if (retry is not null)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    private CancellationTokenSource Limit(CancellationToken cancellationToken)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        return limit;
    }

    private static bool ShouldRetry(Exception ex, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && AssistantError.From(ex).IsTransient;
}
