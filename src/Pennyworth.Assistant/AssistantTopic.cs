namespace Pennyworth.Assistant;

/// <summary>The area a fact, a finding or a question belongs to.</summary>
public enum TopicKind
{
    Overview,
    Storage,
    Memory,
    Processor,
    Graphics,
    Power,
    CleanupItem,
    SpecSection,
}

/// <summary>What a question is about; picks the facts that go into the context.</summary>
/// <param name="Key">The cleanup rule id for <see cref="TopicKind.CleanupItem"/>, the section id for <see cref="TopicKind.SpecSection"/>.</param>
public sealed record AssistantTopic(TopicKind Kind, string? Key = null)
{
    public static AssistantTopic Overview { get; } = new(TopicKind.Overview);
}
