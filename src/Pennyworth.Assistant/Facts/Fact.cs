using System.Globalization;
using Pennyworth.Core.Formatting;

namespace Pennyworth.Assistant;

public enum FactKind
{
    Bytes,
    Percent,
    Count,
    Text,
}

/// <summary>
/// One thing the app measured, with a stable id the diagnosis rules refer to
/// ("disk.system.used_percent") and a label in the user's language.
/// </summary>
/// <param name="Number">The value for rules (bytes, percent, count); 0 for text facts.</param>
/// <param name="Text">The value of <see cref="FactKind.Text"/> facts, already in the user's language.</param>
public sealed record Fact(string Id, TopicKind Group, string Label, FactKind Kind, double Number, string? Text = null)
{
    /// <summary>"74.0 GB", "92%", "1,204", or the text.</summary>
    public string Format(CultureInfo culture) => Kind switch
    {
        FactKind.Bytes => ByteSize.Format((long)Number, culture),
        FactKind.Percent => Number.ToString("0", culture) + "%",
        FactKind.Count => Number.ToString("N0", culture),
        _ => Text ?? "",
    };
}
