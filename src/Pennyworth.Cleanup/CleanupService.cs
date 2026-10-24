using Pennyworth.KnowledgeBase;

namespace Pennyworth.Cleanup;

/// <param name="IsAvailable">False when the rule needs admin rights this app doesn't have yet.</param>
/// <param name="Items">Files found (Recycle Bin: items in it).</param>
public sealed record CleanupEstimate(CleanupRule Rule, long Bytes, long Items, bool IsAvailable);

/// <param name="Skipped">Files left alone because they were in use or protected.</param>
public sealed record CleanupResult(CleanupRule Rule, long BytesFreed, long FilesDeleted, long Skipped);

public interface ICleanupService
{
    IReadOnlyList<CleanupRule> Rules { get; }

    /// <summary>Dry run: what cleaning the rule would free right now.</summary>
    Task<CleanupEstimate> MeasureAsync(CleanupRule rule, CancellationToken cancellationToken);

    /// <summary>Deletes for real. Rules needing admin rights are refused.</summary>
    Task<CleanupResult> CleanAsync(CleanupRule rule, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Measures and cleans what the knowledge base allows. Hard rules: only the contents of the
/// rule's folders are deleted (never the folders), nothing inside Windows or Program Files,
/// links and junctions are never followed, and files in use are skipped.
/// </summary>
public sealed class CleanupService(IReadOnlyList<CleanupRule> rules, Func<string, string?>? getEnvironmentVariable = null) : ICleanupService
{
    private readonly Func<string, string?> _env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public IReadOnlyList<CleanupRule> Rules { get; } = rules;

    public Task<CleanupEstimate> MeasureAsync(CleanupRule rule, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (rule.Kind == CleanupKind.RecycleBin)
        {
            var (bytes, items) = RecycleBin.Query();
            return new CleanupEstimate(rule, bytes, items, IsAvailable: true);
        }

        long total = 0, count = 0;
        foreach (string folder in FoldersOf(rule))
        {
            foreach (FileInfo file in Files(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += SafeLength(file);
                count++;
            }
        }

        return new CleanupEstimate(rule, total, count, IsAvailable: !rule.RequiresAdmin);
    }, cancellationToken);

    public Task<CleanupResult> CleanAsync(CleanupRule rule, IProgress<double>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (rule.RequiresAdmin)
        {
            throw new InvalidOperationException($"Cleanup rule '{rule.Id}' needs admin rights.");
        }

        if (rule.Kind == CleanupKind.RecycleBin)
        {
            var (bytes, items) = RecycleBin.Query();
            bool emptied = RecycleBin.Empty();
            progress?.Report(1);
            return new CleanupResult(rule, emptied ? bytes : 0, emptied ? items : 0, emptied ? 0 : items);
        }

        var folders = FoldersOf(rule);
        var files = folders.SelectMany(folder => Files(folder)).ToList();
        long freed = 0, deleted = 0, skipped = 0;
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long length = SafeLength(files[i]);
            if (TryDelete(files[i]))
            {
                freed += length;
                deleted++;
            }
            else
            {
                skipped++;
            }

            if (i % 64 == 0)
            {
                progress?.Report((double)i / files.Count);
            }
        }

        foreach (string folder in folders)
        {
            RemoveEmptySubfolders(folder);
        }

        progress?.Report(1);
        return new CleanupResult(rule, freed, deleted, skipped);
    }, cancellationToken);

    private IReadOnlyList<string> FoldersOf(CleanupRule rule) =>
        CleanupTargets.Resolve(rule.Paths, _env).Where(folder => SafetyGuard.MayCleanInside(folder, _env)).ToArray();

    private static IEnumerable<FileInfo> Files(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", Walk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            if (file.IsReadOnly)
            {
                file.IsReadOnly = false;
            }

            file.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // In use or protected: leave it.
        }
    }

    /// <summary>Deepest first, so parents empty out; the rule's own folder stays.</summary>
    private static void RemoveEmptySubfolders(string folder)
    {
        string[] subfolders;
        try
        {
            subfolders = Directory.GetDirectories(folder, "*", Walk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string subfolder in subfolders.OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(subfolder).Any())
                {
                    Directory.Delete(subfolder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
