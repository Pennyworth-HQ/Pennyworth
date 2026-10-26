using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssistantEval;

/// <summary>A synthetic PC: what the app measured, what its rules concluded, and questions about it.</summary>
public sealed record Scenario(
    string Id,
    string Title,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> AllowedActions,
    IReadOnlyList<Question> Questions);

public sealed record Fact(string Label, string Value);

/// <param name="Lang">"en" or "pt-BR".</param>
/// <param name="MustSayUnknown">The facts cannot answer it; the answer must say so instead of guessing.</param>
public sealed record Question(string Lang, string Text, bool MustSayUnknown = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(List<Scenario>))]
internal sealed partial class ScenarioJson : JsonSerializerContext;

public static class Scenarios
{
    public static List<Scenario> Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), ScenarioJson.Default.ListScenario)
        ?? throw new InvalidDataException($"No scenarios in {path}");
}
