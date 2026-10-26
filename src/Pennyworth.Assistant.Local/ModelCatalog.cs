using System.Text.Json;
using System.Text.Json.Serialization;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Assistant.Local;

public enum ModelDevice
{
    Cpu,

    /// <summary>A DirectML graphics card.</summary>
    Gpu,
}

/// <param name="Sha256">Lower-case hex; a downloaded file that doesn't match is deleted, never loaded.</param>
public sealed record ModelFile(string Path, long Size, string Sha256);

/// <summary>
/// A local model Pennyworth can download: an exact Hugging Face revision and folder, with every
/// file's size and SHA-256 pinned in the app (spec §10).
/// </summary>
public sealed record LocalModel(
    string Id,
    string DisplayName,
    string Repo,
    string Revision,
    string Folder,
    string Licence,
    ModelDevice Device,
    int MinRamGb,
    int MinVideoMemoryGb,
    IReadOnlyList<string> StopStrings,
    LocalizedText Note,
    IReadOnlyList<ModelFile> Files)
{
    public long Size => Files.Sum(f => f.Size);

    public Uri UrlOf(ModelFile file, Uri baseUrl) => new(baseUrl, $"{Repo}/resolve/{Revision}/{Folder}/{file.Path}");
}

public sealed record ModelCatalog(int Version, IReadOnlyList<LocalModel> Models)
{
    private const string Resource = "Pennyworth.Assistant.Local.models.json";

    public static ModelCatalog Load()
    {
        using Stream stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Embedded resource '{Resource}' is missing.");
        return JsonSerializer.Deserialize(stream, ModelCatalogJsonContext.Default.ModelCatalog)
            ?? throw new InvalidDataException("The model catalogue is empty.");
    }

    public LocalModel Get(string id) => Models.Single(m => m.Id == id);
}

[JsonSerializable(typeof(ModelCatalog))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
internal sealed partial class ModelCatalogJsonContext : JsonSerializerContext;
