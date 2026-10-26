namespace Pennyworth.Assistant;

/// <summary>One piece of a streamed answer, in the order the user should see them.</summary>
public abstract record AssistantUpdate;

/// <summary>Text to append to the answer.</summary>
public sealed record TextUpdate(string Text) : AssistantUpdate;

/// <summary>A button to an app screen, allowed for this context.</summary>
public sealed record ActionUpdate(AssistantAction Action) : AssistantUpdate;

/// <summary>A sentence was held back as unsafe advice; show this line instead.</summary>
public sealed record BlockedUpdate(string Replacement, string Original) : AssistantUpdate;

/// <summary>Tokens the provider reported for the request.</summary>
public sealed record UsageUpdate(long InputTokens, long OutputTokens) : AssistantUpdate;

/// <summary>The first update: the context used and the exact system prompt sent (anonymised for the cloud).</summary>
public sealed record ContextUpdate(ContextBundle Bundle, string SentPrompt, string SentQuestion) : AssistantUpdate;
