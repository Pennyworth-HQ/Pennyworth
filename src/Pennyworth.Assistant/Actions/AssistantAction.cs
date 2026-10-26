namespace Pennyworth.Assistant;

/// <summary>Screens and dialogs the assistant may point to. It never acts by itself.</summary>
public enum ActionKind
{
    /// <summary><c>cleanup</c>: the guided cleanup screen.</summary>
    Cleanup,

    /// <summary><c>cleanup.item:&lt;rule id&gt;</c>: that item's "What will be deleted" preview.</summary>
    CleanupItem,

    /// <summary><c>memory</c>: the Memory &amp; CPU screen.</summary>
    Memory,

    /// <summary><c>close-app:&lt;app key&gt;</c>: the existing "Close app?" dialog.</summary>
    CloseApp,

    /// <summary><c>storage</c>: the storage summary.</summary>
    Storage,

    /// <summary><c>folders:&lt;path&gt;</c>: the folder browser at a path.</summary>
    Folders,

    /// <summary><c>specs</c>: the full specs screen.</summary>
    Specs,

    /// <summary><c>unlock</c>: the full-scan permission dialog.</summary>
    Unlock,
}

/// <summary>An app action written in an answer as <c>[[action:cleanup.item:temp]]</c>.</summary>
public sealed record AssistantAction(ActionKind Kind, string? Argument = null)
{
    private static readonly Dictionary<string, ActionKind> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cleanup"] = ActionKind.Cleanup,
        ["cleanup.item"] = ActionKind.CleanupItem,
        ["memory"] = ActionKind.Memory,
        ["close-app"] = ActionKind.CloseApp,
        ["storage"] = ActionKind.Storage,
        ["folders"] = ActionKind.Folders,
        ["specs"] = ActionKind.Specs,
        ["unlock"] = ActionKind.Unlock,
    };

    /// <summary>"cleanup.item:temp".</summary>
    public string Code => Argument is null ? NameOf(Kind) : $"{NameOf(Kind)}:{Argument}";

    /// <summary>"[[action:cleanup.item:temp]]", as the model writes it.</summary>
    public string Token => $"[[action:{Code}]]";

    /// <summary>Reads "cleanup.item:temp"; null for anything unknown or missing its argument.</summary>
    public static AssistantAction? Parse(string code)
    {
        string trimmed = code.Trim();
        int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        string name = colon < 0 ? trimmed : trimmed[..colon];
        string? argument = colon < 0 ? null : trimmed[(colon + 1)..].Trim();
        if (!Names.TryGetValue(name, out ActionKind kind))
        {
            return null;
        }

        bool needsArgument = kind is ActionKind.CleanupItem or ActionKind.CloseApp or ActionKind.Folders;
        if (needsArgument != !string.IsNullOrEmpty(argument))
        {
            return null;
        }

        return new AssistantAction(kind, needsArgument ? argument : null);
    }

    private static string NameOf(ActionKind kind) => Names.First(pair => pair.Value == kind).Key;
}
