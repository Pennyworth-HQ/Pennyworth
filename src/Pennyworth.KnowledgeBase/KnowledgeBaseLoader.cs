using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Pennyworth.KnowledgeBase;

public static class KnowledgeBaseLoader
{
    private const string ResourceName = "Pennyworth.KnowledgeBase.locations.json";
    private const string ProcessesResource = "Pennyworth.KnowledgeBase.processes.json";
    private const string CleanupResource = "Pennyworth.KnowledgeBase.cleanup.json";

    public static KnowledgeBaseData LoadEmbedded() =>
        Load(ResourceName, KnowledgeBaseJsonContext.Default.KnowledgeBaseData);

    public static KnowledgeBaseData Parse(string json) =>
        JsonSerializer.Deserialize(json, KnowledgeBaseJsonContext.Default.KnowledgeBaseData)
        ?? throw new InvalidDataException("The knowledge base is empty.");

    /// <summary>Plain-language names for Windows and well-known processes.</summary>
    public static ProcessCatalog LoadProcesses() => Load(ProcessesResource, CatalogJsonContext.Default.ProcessCatalog);

    /// <summary>What the cleanup screen offers, with safety levels and consequences.</summary>
    public static CleanupCatalog LoadCleanupRules() => Load(CleanupResource, CatalogJsonContext.Default.CleanupCatalog);

    private static T Load<T>(string resourceName, JsonTypeInfo<T> typeInfo)
    {
        using Stream stream = typeof(KnowledgeBaseLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' is missing.");
        return JsonSerializer.Deserialize(stream, typeInfo)
            ?? throw new InvalidDataException($"The knowledge base file '{resourceName}' is empty.");
    }
}
