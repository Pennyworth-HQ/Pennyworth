using System.ClientModel;

namespace Pennyworth.Assistant.Providers;

public enum AssistantErrorKind
{
    /// <summary>The key was refused (401/403).</summary>
    BadKey,

    /// <summary>The model or deployment doesn't exist (404).</summary>
    UnknownModel,

    /// <summary>Rate limit or credits used up (429/402).</summary>
    Quota,

    /// <summary>No connection, DNS, TLS…</summary>
    Network,

    /// <summary>The provider took too long.</summary>
    Timeout,

    /// <summary>The provider failed (5xx).</summary>
    Server,

    /// <summary>The person pressed Stop.</summary>
    Cancelled,

    Unknown,
}

/// <summary>An HTTP error from a provider, with the status and the provider's own message.</summary>
public sealed class ProviderException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>A failure as the UI explains it, with the technical detail for the "Details" expander. Never contains the key.</summary>
public sealed record AssistantError(AssistantErrorKind Kind, string Detail)
{
    public bool IsTransient => Kind is AssistantErrorKind.Network or AssistantErrorKind.Timeout or AssistantErrorKind.Server;

    public static AssistantError From(Exception exception, CancellationToken userCancellation = default) => exception switch
    {
        OperationCanceledException when userCancellation.IsCancellationRequested => new(AssistantErrorKind.Cancelled, ""),
        OperationCanceledException or TimeoutException => new(AssistantErrorKind.Timeout, exception.Message),
        ProviderException p => new(FromStatus(p.Status), $"HTTP {p.Status}: {p.Message}"),
        ClientResultException c when c.Status > 0 => new(FromStatus(c.Status), $"HTTP {c.Status}: {FirstLine(c.Message)}"),
        HttpRequestException { StatusCode: { } status } h => new(FromStatus((int)status), $"HTTP {(int)status}: {h.Message}"),
        HttpRequestException or ClientResultException or IOException => new(AssistantErrorKind.Network, exception.Message),
        _ => new(AssistantErrorKind.Unknown, $"{exception.GetType().Name}: {exception.Message}"),
    };

    private static AssistantErrorKind FromStatus(int status) => status switch
    {
        401 or 403 => AssistantErrorKind.BadKey,
        404 => AssistantErrorKind.UnknownModel,
        402 or 429 => AssistantErrorKind.Quota,
        408 => AssistantErrorKind.Timeout,
        >= 500 => AssistantErrorKind.Server,
        _ => AssistantErrorKind.Unknown,
    };

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
}
