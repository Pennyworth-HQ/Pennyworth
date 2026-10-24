using Pennyworth.Core.Storage;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Insights.Classification;

/// <summary>Assigns categories from the knowledge base's known locations.</summary>
public sealed class RuleClassifier : IPathClassifier
{
    private readonly (PathPattern Pattern, KnownLocation Location)[] _rules;

    /// <param name="getEnvironmentVariable">Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.</param>
    public RuleClassifier(IEnumerable<KnownLocation> locations, Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        _rules = locations
            .Select(location => (Expanded: ExpandVariables(location.Path, getEnvironmentVariable), Location: location))
            .Where(rule => rule.Expanded is not null)
            .Select(rule => (PathPattern.Parse(rule.Expanded!), rule.Location))
            .OrderByDescending(rule => rule.Item1.Specificity)
            .ToArray();
    }

    public StorageCategory Classify(string fullPath, StorageCategory inherited) =>
        FindLocation(fullPath)?.Category ?? inherited;

    /// <summary>The most specific known location that matches exactly this path, if any.</summary>
    public KnownLocation? FindLocation(string fullPath)
    {
        string[] segments = PathPattern.SplitPath(fullPath);
        foreach (var (pattern, location) in _rules)
        {
            if (pattern.IsMatch(segments))
            {
                return location;
            }
        }

        return null;
    }

    /// <summary>Expands <c>%NAME%</c> variables; returns null when one is not defined.</summary>
    internal static string? ExpandVariables(string path, Func<string, string?> getEnvironmentVariable)
    {
        var result = new System.Text.StringBuilder();
        int index = 0;
        while (index < path.Length)
        {
            int start = path.IndexOf('%', index);
            if (start < 0)
            {
                result.Append(path, index, path.Length - index);
                break;
            }

            int end = path.IndexOf('%', start + 1);
            if (end < 0)
            {
                throw new FormatException($"Unclosed environment variable in '{path}'.");
            }

            string? value = getEnvironmentVariable(path[(start + 1)..end]);
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            result.Append(path, index, start - index).Append(value);
            index = end + 1;
        }

        return result.ToString();
    }
}
