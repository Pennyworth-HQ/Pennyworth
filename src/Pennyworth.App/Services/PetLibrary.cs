using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Pennyworth.App.Services;

/// <summary>
/// A Codex pet, as Petdex and the other pet sites share them: a <c>pet.json</c> and a spritesheet
/// of 192×208 frames, 8 columns by 9 rows (or 11 in the v2 atlas), one row per animation.
/// </summary>
public sealed record PetPackage(string Id, string DisplayName, string Description, string SpritesheetPath, int Rows);

public enum PetImportError
{
    /// <summary>No pet.json and no spritesheet: not a pet at all.</summary>
    NotAPet,

    /// <summary>The image is not an 8×9 or 8×11 grid of pet frames.</summary>
    BadSpritesheet,

    /// <summary>The file could not be read or the pet could not be saved.</summary>
    Unreadable,
}

public sealed record PetImportResult(PetPackage? Pet, PetImportError? Error)
{
    public static PetImportResult Failed(PetImportError error) => new(null, error);
}

/// <summary>
/// The pets the person added, one folder each in <c>%LocalAppData%\Pennyworth\pets</c>. Imports a
/// downloaded pet as it comes: the .zip, the unzipped folder, its pet.json, or a bare spritesheet
/// (a ChatGPT pet export has no pet.json).
/// </summary>
public sealed class PetLibrary(string root)
{
    public const int Columns = 8;
    public const int FrameWidth = 192;
    public const int FrameHeight = 208;

    /// <summary>Real spritesheets are a few MB; anything far bigger is not a pet (or a zip bomb).</summary>
    private const long MaxFileBytes = 64L * 1024 * 1024;

    private static readonly string[] SheetNames = ["spritesheet.webp", "spritesheet.png"];

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pennyworth", "pets");

    /// <summary>The file picker's filter: what <see cref="Import"/> accepts.</summary>
    public static IReadOnlyList<string> Patterns { get; } = ["*.zip", "pet.json", "*.webp", "*.png"];

    public PetImportResult Import(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return ImportFolder(path);
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension switch
            {
                ".zip" => ImportZip(path),
                ".json" => ImportFolder(Path.GetDirectoryName(Path.GetFullPath(path))!),
                ".webp" or ".png" => Save(null, Path.GetFileNameWithoutExtension(path), ReadLimited(File.OpenRead(path)), extension),
                _ => PetImportResult.Failed(PetImportError.NotAPet),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return PetImportResult.Failed(PetImportError.Unreadable);
        }
    }

    /// <summary>A pet added before, or null when its folder is gone or broken.</summary>
    public PetPackage? Load(string id)
    {
        try
        {
            string folder = Path.Combine(root, Slug(id));
            string json = File.ReadAllText(Path.Combine(folder, "pet.json"));
            PetJson meta = PetJson.Parse(json);
            string sheet = Path.Combine(folder, Path.GetFileName(meta.SpritesheetPath ?? SheetNames[0]));
            using FileStream stream = File.OpenRead(sheet);
            Span<byte> header = stackalloc byte[32];
            int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return SpritesheetGrid.Rows(header[..read]) is { } rows
                ? new PetPackage(Slug(id), meta.DisplayName ?? id, meta.Description ?? "", sheet, rows)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private PetImportResult ImportFolder(string folder)
    {
        string petJson = Path.Combine(folder, "pet.json");
        PetJson? meta = File.Exists(petJson) ? PetJson.TryParse(File.ReadAllText(petJson)) : null;
        string? sheet = new[] { meta?.SpritesheetPath }.Concat(SheetNames)
            .Where(name => name is not null)
            .Select(name => Path.Combine(folder, Path.GetFileName(name!)))
            .FirstOrDefault(File.Exists);
        if (sheet is null)
        {
            return PetImportResult.Failed(PetImportError.NotAPet);
        }

        using FileStream stream = File.OpenRead(sheet);
        return Save(meta, Path.GetFileName(folder), ReadLimited(stream), Path.GetExtension(sheet));
    }

    private PetImportResult ImportZip(string path)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);

        // The pet.json closest to the root: a pack may wrap the pet in a folder of its name.
        ZipArchiveEntry? petJson = zip.Entries
            .Where(e => string.Equals(e.Name, "pet.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c is '/' or '\\'))
            .FirstOrDefault();
        string prefix = petJson is null ? "" : petJson.FullName[..^petJson.Name.Length];
        PetJson? meta = petJson is null ? null : PetJson.TryParse(Encoding.UTF8.GetString(ReadLimited(petJson.Open(), petJson.Length)));

        // Entries are only looked up by name and read into memory; nothing is extracted to a path
        // the archive chose.
        ZipArchiveEntry? sheet = new[] { meta?.SpritesheetPath }.Concat(SheetNames)
            .Where(name => name is not null)
            .Select(name => zip.GetEntry(prefix + Path.GetFileName(name!)))
            .FirstOrDefault(e => e is not null)
            ?? (petJson is null ? zip.Entries.FirstOrDefault(e => SheetNames.Contains(e.Name, StringComparer.OrdinalIgnoreCase)) : null);
        if (sheet is null)
        {
            return PetImportResult.Failed(PetImportError.NotAPet);
        }

        return Save(meta, Path.GetFileNameWithoutExtension(path), ReadLimited(sheet.Open(), sheet.Length), Path.GetExtension(sheet.Name));
    }

    /// <summary>Checks the grid and writes the pet to its own folder, replacing an older copy.</summary>
    private PetImportResult Save(PetJson? meta, string fallbackName, byte[] sheet, string extension)
    {
        if (SpritesheetGrid.Rows(sheet) is not { } rows)
        {
            return PetImportResult.Failed(PetImportError.BadSpritesheet);
        }

        string id = Slug(meta?.Id ?? fallbackName);
        string displayName = string.IsNullOrWhiteSpace(meta?.DisplayName) ? Title(fallbackName) : meta.DisplayName.Trim();
        string sheetName = "spritesheet" + extension.ToLowerInvariant();
        string folder = Path.Combine(root, id);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        string sheetPath = Path.Combine(folder, sheetName);
        File.WriteAllBytes(sheetPath, sheet);
        File.WriteAllText(Path.Combine(folder, "pet.json"), new PetJson(id, displayName, meta?.Description ?? "", sheetName).ToJson());
        return new PetImportResult(new PetPackage(id, displayName, meta?.Description ?? "", sheetPath, rows), null);
    }

    private static byte[] ReadLimited(Stream stream, long declaredLength = -1)
    {
        using (stream)
        {
            if (declaredLength > MaxFileBytes || (stream.CanSeek && stream.Length > MaxFileBytes))
            {
                throw new InvalidDataException("The pet file is too large.");
            }

            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxFileBytes)
                {
                    throw new InvalidDataException("The pet file is too large.");
                }
            }

            return buffer.ToArray();
        }
    }

    /// <summary>"Boba Tea!" → "boba-tea": a folder name that is safe on every file system.</summary>
    internal static string Slug(string name)
    {
        var slug = new StringBuilder();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        string result = slug.ToString().Trim('-');
        return result.Length == 0 ? "pet" : result.Length > 64 ? result[..64].TrimEnd('-') : result;
    }

    /// <summary>"boba-tea" → "Boba tea", for a pet that came without a display name.</summary>
    private static string Title(string name)
    {
        string text = name.Replace('-', ' ').Replace('_', ' ').Trim();
        return text.Length == 0 ? "Pet" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private sealed record PetJson(string? Id, string? DisplayName, string? Description, string? SpritesheetPath)
    {
        public static PetJson? TryParse(string json)
        {
            try
            {
                return Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static PetJson Parse(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("pet.json is not an object.");
            }

            return new PetJson(Text(root, "id") ?? Text(root, "slug"), Text(root, "displayName") ?? Text(root, "name"), Text(root, "description"), Text(root, "spritesheetPath"));
        }

        public string ToJson()
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("id", Id);
                writer.WriteString("displayName", DisplayName);
                writer.WriteString("description", Description);
                writer.WriteString("spritesheetPath", SpritesheetPath);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

/// <summary>Recognises a pet spritesheet from its PNG or WebP header, without decoding pixels.</summary>
public static class SpritesheetGrid
{
    /// <summary>9 or 11 rows for a pet grid (or a clean scale of one), otherwise null.</summary>
    public static int? Rows(ReadOnlySpan<byte> header) =>
        Size(header) is var (width, height) ? RowsFor(width, height) : null;

    public static int? RowsFor(int width, int height)
    {
        foreach (int rows in (ReadOnlySpan<int>)[9, 11])
        {
            long canonicalWidth = PetLibrary.Columns * PetLibrary.FrameWidth;
            long canonicalHeight = rows * PetLibrary.FrameHeight;
            if (width > 0 && height > 0 && width % PetLibrary.Columns == 0 && height % rows == 0
                && width * canonicalHeight == height * canonicalWidth)
            {
                return rows;
            }
        }

        return null;
    }

    /// <summary>Width and height from a PNG IHDR or a WebP VP8/VP8L/VP8X header.</summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])
            && data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return (BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadInt32BigEndian(data[20..]));
        }

        if (data.Length < 30 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return null;
        }

        ReadOnlySpan<byte> chunk = data.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            return (1 + (data[24] | (data[25] << 8) | (data[26] << 16)), 1 + (data[27] | (data[28] << 8) | (data[29] << 16)));
        }

        if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2F)
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8 "u8) && data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF);
        }

        return null;
    }
}
