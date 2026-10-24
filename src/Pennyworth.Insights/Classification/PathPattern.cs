namespace Pennyworth.Insights.Classification;

/// <summary>
/// A path pattern split into folder names. Supports <c>?:</c> (any drive) as the first
/// segment, <c>*</c> (any one folder name) and a leading <c>**</c> (any prefix).
/// Matching is case-insensitive. Environment variables must already be expanded.
/// </summary>
public sealed class PathPattern
{
    private readonly string[] _segments;
    private readonly bool _anyPrefix;

    private PathPattern(string[] segments, bool anyPrefix)
    {
        _segments = segments;
        _anyPrefix = anyPrefix;
    }

    /// <summary>Higher wins when several patterns match the same path.</summary>
    public int Specificity => (_segments.Length * 2) + (_anyPrefix ? 0 : 1);

    public static PathPattern Parse(string pattern)
    {
        string[] segments = SplitPath(pattern);
        bool anyPrefix = segments.Length > 0 && segments[0] == "**";
        if (anyPrefix)
        {
            segments = segments[1..];
        }

        if (segments.Length == 0 || segments.Contains("**"))
        {
            throw new FormatException($"Invalid path pattern '{pattern}'.");
        }

        return new PathPattern(segments, anyPrefix);
    }

    public static string[] SplitPath(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    public bool IsMatch(string[] pathSegments)
    {
        if (_anyPrefix ? pathSegments.Length < _segments.Length : pathSegments.Length != _segments.Length)
        {
            return false;
        }

        int offset = pathSegments.Length - _segments.Length;
        for (int i = 0; i < _segments.Length; i++)
        {
            if (!SegmentMatches(_segments[i], pathSegments[offset + i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SegmentMatches(string pattern, string segment) => pattern switch
    {
        "*" => true,
        "?:" => segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':',
        _ => string.Equals(pattern, segment, StringComparison.OrdinalIgnoreCase),
    };
}
