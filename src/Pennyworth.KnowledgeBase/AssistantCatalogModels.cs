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

[JsonSerializable(typeof(DiagnosisCatalog))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
internal sealed partial class AssistantCatalogJsonContext : JsonSerializerContext;
