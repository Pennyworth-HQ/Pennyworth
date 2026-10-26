namespace Pennyworth.Assistant.Local;

/// <summary>
/// Downloaded models under <c>%LocalAppData%\Pennyworth\models\&lt;id&gt;\&lt;revision&gt;</c>. A model
/// counts as installed only when its folder carries the verified marker for its revision and
/// every file has its catalogued size.
/// </summary>
public sealed class LocalModelStore(string root)
{
    private const string Marker = ".verified";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pennyworth", "models");

    public string FolderOf(LocalModel model) => Path.Combine(root, model.Id, model.Revision[..7]);

    public bool IsInstalled(LocalModel model)
    {
        string folder = FolderOf(model);
        string marker = Path.Combine(folder, Marker);
        return File.Exists(marker)
            && File.ReadAllText(marker).Trim() == model.Revision
            && model.Files.All(f => new FileInfo(Path.Combine(folder, f.Path)) is { Exists: true } info && info.Length == f.Size);
    }

    /// <summary>Space the model takes on disk now, including unfinished downloads.</summary>
    public long SizeOnDisk(LocalModel model)
    {
        string folder = Path.Combine(root, model.Id);
        return Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;
    }

    /// <summary>"Remove model" in Settings: frees the space, every revision.</summary>
    public void Remove(LocalModel model)
    {
        string folder = Path.Combine(root, model.Id);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    internal static void MarkVerified(string folder, LocalModel model) => File.WriteAllText(Path.Combine(folder, Marker), model.Revision);

    internal static void Unverify(string folder)
    {
        string marker = Path.Combine(folder, Marker);
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }
    }
}
