using System.Text.Json.Serialization;

namespace Pennyworth.KnowledgeBase;

/// <summary>
/// A plain-language name for processes users don't recognise ("svchost.exe" → "Windows services").
/// </summary>
/// <param name="Names">Executable names without ".exe" (or the kernel's name, such as "Memory Compression"); case-insensitive.</param>
/// <param name="IsProtected">Part of Windows: shown with a lock and never offered for closing.</param>
/// <param name="CanClose">Offer "Close app". False for protected entries and for things closed elsewhere (WSL).</param>
/// <param name="ListServices">Expand into the Windows services running in each process.</param>
public sealed record ProcessExplanation(
    string Id,
    IReadOnlyList<string> Names,
    LocalizedText Title,
    LocalizedText Subtitle,
    LocalizedText? Note,
    bool IsProtected,
    bool CanClose,
    bool ListServices = false);

public sealed record ProcessCatalog(int Version, IReadOnlyList<ProcessExplanation> Processes);

public enum SafetyLevel
{
    /// <summary>Recreated automatically. Pre-selected.</summary>
    Safe,

    /// <summary>Fine for most people; a small trade-off is explained.</summary>
    UsuallySafe,

    /// <summary>A real consequence.</summary>
    ThinkFirst,
}

public enum CleanupKind
{
    /// <summary>Deletes what is inside the matching folders, never the folders themselves.</summary>
    FolderContents,

    /// <summary>Empties the Recycle Bin through Windows.</summary>
    RecycleBin,
}

/// <summary>
/// Something that can be cleaned. Paths use <c>%ENV%</c> variables and <c>*</c> for one folder name.
/// </summary>
/// <param name="RequiresAdmin">Shown and measured when possible, but only cleanable with the full-scan permission.</param>
public sealed record CleanupRule(
    string Id,
    SafetyLevel Safety,
    CleanupKind Kind,
    IReadOnlyList<string> Paths,
    bool RequiresAdmin,
    LocalizedText Title,
    LocalizedText Description,
    LocalizedText Consequence);

public sealed record CleanupCatalog(int Version, IReadOnlyList<CleanupRule> Rules);

[JsonSerializable(typeof(ProcessCatalog))]
[JsonSerializable(typeof(CleanupCatalog))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
