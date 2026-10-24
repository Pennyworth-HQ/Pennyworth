using System.Text.Json;

namespace Pennyworth.KnowledgeBase;

public static class KnowledgeBaseLoader
{
    private const string ResourceName = "Pennyworth.KnowledgeBase.locations.json";

    public static KnowledgeBaseData LoadEmbedded()
    {
        using Stream stream = typeof(KnowledgeBaseLoader).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        return JsonSerializer.Deserialize(stream, KnowledgeBaseJsonContext.Default.KnowledgeBaseData)
            ?? throw new InvalidDataException("The knowledge base is empty.");
    }

    public static KnowledgeBaseData Parse(string json) =>
        JsonSerializer.Deserialize(json, KnowledgeBaseJsonContext.Default.KnowledgeBaseData)
        ?? throw new InvalidDataException("The knowledge base is empty.");
}
