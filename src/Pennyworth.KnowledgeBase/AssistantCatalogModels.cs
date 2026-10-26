using System.Text.Json.Serialization;

namespace Pennyworth.KnowledgeBase;

public enum DiagnosisSeverity
{
    /// <summary>Worth knowing; nothing is wrong.</summary>
    Info,

    /// <summary>Slows the PC down or will soon.</summary>
    Warning,

    /// <summary>Hurting the PC right now.</summary>
    Problem,
}

/// <summary>A test on one fact. <see cref="Op"/> is <c>gt</c>, <c>gte</c>, <c>lt</c>, <c>lte</c>, <c>eq</c> or <c>exists</c>.</summary>
public sealed record FactCondition(string Fact, string Op, double Value = 0);

/// <summary>
/// What the assistant concludes when every condition holds. The finding may name facts as
/// <c>{fact.id}</c>; they are filled in the user's language.
/// </summary>
/// <param name="Group">The topic the finding belongs to: <c>Overview</c>, <c>Storage</c>, <c>Memory</c>, <c>Processor</c>, <c>Graphics</c> or <c>Power</c>.</param>
/// <param name="Action">An app action the finding can point to, as in <c>[[action:…]]</c>.</param>
public sealed record DiagnosisRule(
    string Id,
    DiagnosisSeverity Severity,
    string Group,
    IReadOnlyList<FactCondition> When,
    LocalizedText Finding,
    string? Action = null);

public sealed record DiagnosisCatalog(int Version, IReadOnlyList<DiagnosisRule> Rules);

/// <summary>Advice the assistant must never give, as case-insensitive regular expressions (English and Portuguese).</summary>
public sealed record BlockedAdvice(string Id, IReadOnlyList<string> Patterns);

/// <summary>
/// The assistant's safety net. A sentence matching a pattern is replaced, unless it is a refusal:
/// one of <see cref="Negations"/> comes before the advice ("I wouldn't disable Windows Security"),
/// or one of <see cref="Warnings"/> appears anywhere ("Editing the registry is risky").
/// </summary>
public sealed record AdviceCatalog(
    int Version,
    LocalizedText Replacement,
    IReadOnlyList<string> Negations,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<BlockedAdvice> Topics);

[JsonSerializable(typeof(DiagnosisCatalog))]
[JsonSerializable(typeof(AdviceCatalog))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
internal sealed partial class AssistantCatalogJsonContext : JsonSerializerContext;
