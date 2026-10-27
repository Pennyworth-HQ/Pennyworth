namespace Pennyworth.Cleanup;

/// <summary>Turns a rule's path patterns into the folders that exist on this PC.</summary>
internal static class CleanupTargets
{
    /// <summary>
    /// Expands <c>%VAR%</c> (patterns with an undefined variable are skipped) and <c>*</c>
    /// (any one folder name). Returns existing, distinct folders; two paths are the same folder
    /// when <paramref name="pathComparison"/> says so (ignoring case unless the file system cares).
    /// </summary>
    public static IReadOnlyList<string> Resolve(
        IEnumerable<string> patterns, Func<string, string?> getEnvironmentVariable, StringComparer? pathComparison = null)
    {
        var folders = new SortedSet<string>(pathComparison ?? StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Walks an absolute pattern down from its real root, which comes from
    /// <see cref="Path.GetPathRoot(string)"/> rather than from the first segment: only drive letters
    /// double as a root, so a UNC share or a POSIX path used to be turned into a relative walk that
    /// matched nothing.
    /// </summary>
    private static IEnumerable<string> Match(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            yield break;
        }

        string[] segments = path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> current = [root];
        foreach (string segment in segments)
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
    /// False for drive roots, the user profile itself, Windows and Program Files (and anything inside
    /// them). Also false when a location that always exists on Windows cannot be named: an unanswered
    /// safety question is not a "yes", so a broken environment refuses everything rather than
    /// protecting nothing.
    /// </summary>
    public static bool MayCleanInside(string folder, Func<string, string?> getEnvironmentVariable)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (IsDriveRoot(folder)
            || (Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // These three always exist on Windows, so an environment that cannot name them cannot be
        // trusted to say what is safe.
        string? profile = getEnvironmentVariable("USERPROFILE");
        string? windows = getEnvironmentVariable("SystemRoot");
        string? programs = getEnvironmentVariable("ProgramFiles");
        if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(windows) || string.IsNullOrEmpty(programs))
        {
            return false;
        }

        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)), full, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string protectedFolder in ProtectedRoots(windows, programs, getEnvironmentVariable))
        {
            if (IsSameOrInside(full, protectedFolder))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The folders nothing may be cleaned inside. On a 64-bit install "Program Files (x86)" sits
    /// beside the Windows folder whether or not its variable reached this process, so it is derived
    /// from the resolved Windows path rather than silently dropped. The variable itself is absent on
    /// a 32-bit install, where the folder does not exist either.
    /// </summary>
    private static IEnumerable<string> ProtectedRoots(string windows, string programs, Func<string, string?> getEnvironmentVariable)
    {
        yield return windows;
        yield return programs;

        if (getEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } programsX86)
        {
            yield return programsX86;
        }
        else
        {
            int cut = windows.LastIndexOfAny(['\\', '/']);
            if (cut > 0)
            {
                yield return windows[..(cut + 1)] + @"Program Files (x86)";
            }
        }

        if (getEnvironmentVariable("ProgramW6432") is { Length: > 0 } programs6432)
        {
            yield return programs6432;
        }
    }

    /// <summary>
    /// "C:" or "C:\", however the caller wrote it. Checked on the raw input so the answer does not
    /// depend on how the host resolves a Windows path.
    /// </summary>
    private static bool IsDriveRoot(string folder)
    {
        string trimmed = folder.TrimEnd('\\', '/');
        return trimmed.Length == 2 && trimmed[1] == ':' && char.IsAsciiLetter(trimmed[0]);
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return path.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent + '\\', StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent + '/', StringComparison.OrdinalIgnoreCase);
    }
}
