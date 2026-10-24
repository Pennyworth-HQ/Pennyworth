namespace Pennyworth.Cleanup;

/// <summary>Turns a rule's path patterns into the folders that exist on this PC.</summary>
internal static class CleanupTargets
{
    /// <summary>
    /// Expands <c>%VAR%</c> (patterns with an undefined variable are skipped) and <c>*</c>
    /// (any one folder name). Returns existing, distinct folders.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<string> patterns, Func<string, string?> getEnvironmentVariable)
    {
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pattern in patterns)
        {
            string? expanded = Expand(pattern, getEnvironmentVariable);
            if (expanded is null)
            {
                continue;
            }

            foreach (string folder in Match(expanded))
            {
                folders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
            }
        }

        return [.. folders];
    }

    private static string? Expand(string pattern, Func<string, string?> getEnvironmentVariable)
    {
        var result = new System.Text.StringBuilder();
        int index = 0;
        while (index < pattern.Length)
        {
            int start = pattern.IndexOf('%', index);
            if (start < 0)
            {
                result.Append(pattern, index, pattern.Length - index);
                break;
            }

            int end = pattern.IndexOf('%', start + 1);
            if (end < 0)
            {
                return null;
            }

            string? value = getEnvironmentVariable(pattern[(start + 1)..end]);
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            result.Append(pattern, index, start - index).Append(value);
            index = end + 1;
        }

        return result.ToString();
    }

    private static IEnumerable<string> Match(string path)
    {
        string[] segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            yield break;
        }

        IEnumerable<string> current = [segments[0] + Path.DirectorySeparatorChar];
        foreach (string segment in segments.Skip(1))
        {
            current = segment == "*"
                ? current.SelectMany(SafeSubdirectories)
                : current.Select(parent => Path.Combine(parent, segment)).Where(Directory.Exists);
        }

        foreach (string folder in current)
        {
            if (Directory.Exists(folder))
            {
                yield return folder;
            }
        }
    }

    private static IEnumerable<string> SafeSubdirectories(string parent)
    {
        try
        {
            return Directory.EnumerateDirectories(parent).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>The hard rules from the spec, enforced no matter what a rule says.</summary>
internal static class SafetyGuard
{
    /// <summary>
    /// False for drive roots, the user profile itself, Windows and Program Files (and anything inside them).
    /// </summary>
    public static bool MayCleanInside(string folder, Func<string, string?> getEnvironmentVariable)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (getEnvironmentVariable("USERPROFILE") is { Length: > 0 } profile
            && string.Equals(Path.TrimEndingDirectorySeparator(profile), full, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string variable in (string[])["SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432"])
        {
            if (getEnvironmentVariable(variable) is { Length: > 0 } protectedFolder && IsSameOrInside(full, protectedFolder))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return path.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
