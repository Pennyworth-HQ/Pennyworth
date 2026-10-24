using System.Globalization;
using System.Text.Json.Serialization;
using Pennyworth.Core.Storage;

namespace Pennyworth.KnowledgeBase;

public sealed record LocalizedText(
    string En,
    [property: JsonPropertyName("pt-BR")] string PtBr)
{
    public string Get(CultureInfo culture) =>
        culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? PtBr : En;
}

/// <summary>
/// A known folder or root file. <see cref="Path"/> supports <c>%ENV%</c> variables,
/// <c>?:</c> for any drive, <c>*</c> for one folder name and a leading <c>**</c> for any prefix.
/// The category applies to the matched item and everything below it.
/// </summary>
public sealed record KnownLocation(
    string Id,
    string Path,
    StorageCategory Category,
    LocalizedText Title,
    LocalizedText Description);

public sealed record KnowledgeBaseData(int Version, IReadOnlyList<KnownLocation> Locations);

[JsonSerializable(typeof(KnowledgeBaseData))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
internal sealed partial class KnowledgeBaseJsonContext : JsonSerializerContext;
