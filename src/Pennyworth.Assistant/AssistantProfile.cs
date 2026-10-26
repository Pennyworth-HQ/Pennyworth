namespace Pennyworth.Assistant;

public enum AssistantBackend
{
    /// <summary>A model downloaded to this PC; nothing leaves it.</summary>
    Local,
    OpenAiCompatible,
    Anthropic,
    Gemini,
    AzureOpenAi,
}

/// <summary>
/// One configured way to answer. The API key is never part of the profile; it lives in the
/// Windows Credential Manager under the profile's id.
/// </summary>
/// <param name="ModelId">The provider's model id, the Azure deployment name, or the local model's folder name.</param>
/// <param name="MonthlyTokenCap">Optional limit on input + output tokens per calendar month.</param>
public sealed record AssistantProfile(
    string Id,
    AssistantBackend Backend,
    string DisplayName,
    string ModelId,
    string? Endpoint = null,
    long? MonthlyTokenCap = null)
{
    /// <summary>Local answers need no send preview and no anonymisation.</summary>
    public bool IsLocal => Backend == AssistantBackend.Local;
}
