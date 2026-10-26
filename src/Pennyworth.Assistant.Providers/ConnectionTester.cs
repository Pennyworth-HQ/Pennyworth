using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace Pennyworth.Assistant.Providers;

/// <param name="Reply">What the model answered, for the person to see it really talks.</param>
public sealed record ConnectionResult(bool Ok, TimeSpan Latency, string? Reply, AssistantError? Error);

/// <summary>"Test connection": one tiny request, so a wrong key or model shows up before the first real question.</summary>
public static class ConnectionTester
{
    public static async Task<ConnectionResult> TestAsync(IChatClient client, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            ChatResponse response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word OK.")],
                new ChatOptions { MaxOutputTokens = 10, Temperature = 0 },
                cancellationToken);
            return new ConnectionResult(true, watch.Elapsed, response.Text.Trim(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ConnectionResult(false, watch.Elapsed, null, AssistantError.From(ex, cancellationToken));
        }
    }
}
