using System.Buffers.Binary;
using System.IO.Compression;
using Pennyworth.App.Services;

namespace Pennyworth.App.Tests;

/// <summary>Codex pets as Petdex ships them, built on disk without real images.</summary>
internal static class SamplePets
{
    public const string PetJson = """{ "id": "boba", "displayName": "Boba", "description": "A tapioca pearl.", "spritesheetPath": "spritesheet.webp" }""";

    /// <summary>The first bytes of a real Petdex download: a lossless WebP of 1536×1872.</summary>
    public static readonly byte[] WebpHeader = Convert.FromHexString("524946462C9A1F00574542505650384C209A1F002FFFC5D311118B6D24099224");

    /// <summary>A PNG header of the given size: enough for the grid check, which never decodes pixels.</summary>
    public static byte[] Png(int width, int height)
    {
        byte[] data = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), 13);
        "IHDR"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), height);
        return data;
    }

    public static string Zip(string dir, string name, params (string Path, byte[] Data)[] entries)
    {
        string path = Path.Combine(dir, name);
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string entryPath, byte[] data) in entries)
        {
            using Stream stream = zip.CreateEntry(entryPath).Open();
            stream.Write(data);
        }

        return path;
    }

    public static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}

public sealed class PetLibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pennyworth-pets").FullName;

    private string Library => Path.Combine(_dir, "library");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_petdex_zip_is_added_with_its_name()
    {
        string zip = SamplePets.Zip(_dir, "boba.zip", ("pet.json", SamplePets.Utf8(SamplePets.PetJson)), ("spritesheet.webp", SamplePets.WebpHeader));

        PetImportResult result = new PetLibrary(Library).Import(zip);

        PetPackage pet = Assert.IsType<PetPackage>(result.Pet);
        Assert.Equal("boba", pet.Id);
        Assert.Equal("Boba", pet.DisplayName);
        Assert.Equal(9, pet.Rows);
        Assert.Equal(Path.Combine(Library, "boba", "spritesheet.webp"), pet.SpritesheetPath);
        Assert.True(File.Exists(pet.SpritesheetPath));
    }

    [Fact]
    public void A_zip_with_the_pet_in_a_folder_is_added()
    {
        string zip = SamplePets.Zip(_dir, "pack.zip", ("boba/pet.json", SamplePets.Utf8(SamplePets.PetJson)), ("boba/spritesheet.webp", SamplePets.WebpHeader));

        Assert.Equal("boba", new PetLibrary(Library).Import(zip).Pet?.Id);
    }

    [Fact]
    public void An_added_pet_loads_again_after_a_restart()
    {
        string zip = SamplePets.Zip(_dir, "boba.zip", ("pet.json", SamplePets.Utf8(SamplePets.PetJson)), ("spritesheet.webp", SamplePets.WebpHeader));
        new PetLibrary(Library).Import(zip);

        PetPackage? pet = new PetLibrary(Library).Load("boba");

        Assert.Equal("Boba", pet?.DisplayName);
        Assert.Equal("A tapioca pearl.", pet?.Description);
    }

    [Fact]
    public void An_unzipped_folder_or_its_pet_json_is_added()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "boba")).FullName;
        File.WriteAllText(Path.Combine(folder, "pet.json"), SamplePets.PetJson);
        File.WriteAllBytes(Path.Combine(folder, "spritesheet.webp"), SamplePets.WebpHeader);
        var library = new PetLibrary(Library);

        Assert.Equal("boba", library.Import(folder).Pet?.Id);
        Assert.Equal("boba", library.Import(Path.Combine(folder, "pet.json")).Pet?.Id);
    }

    [Fact]
    public void A_bare_v2_spritesheet_is_named_after_its_file()
    {
        string sheet = Path.Combine(_dir, "my-cat.png");
        File.WriteAllBytes(sheet, SamplePets.Png(1536, 2288));

        PetPackage pet = Assert.IsType<PetPackage>(new PetLibrary(Library).Import(sheet).Pet);

        Assert.Equal("my-cat", pet.Id);
        Assert.Equal("My cat", pet.DisplayName);
        Assert.Equal(11, pet.Rows);
    }

    [Fact]
    public void An_image_that_is_not_a_pet_grid_is_refused()
    {
        string sheet = Path.Combine(_dir, "photo.png");
        File.WriteAllBytes(sheet, SamplePets.Png(1920, 1080));

        Assert.Equal(PetImportError.BadSpritesheet, new PetLibrary(Library).Import(sheet).Error);
        Assert.False(Directory.Exists(Library));
    }

    [Fact]
    public void A_zip_without_a_pet_is_refused()
    {
        string zip = SamplePets.Zip(_dir, "notes.zip", ("readme.txt", SamplePets.Utf8("hello")));

        Assert.Equal(PetImportError.NotAPet, new PetLibrary(Library).Import(zip).Error);
    }

    [Fact]
    public void A_broken_zip_is_unreadable()
    {
        string zip = Path.Combine(_dir, "broken.zip");
        File.WriteAllText(zip, "not a zip");

        Assert.Equal(PetImportError.Unreadable, new PetLibrary(Library).Import(zip).Error);
    }

    [Fact]
    public void A_pet_id_cannot_escape_the_library()
    {
        string json = """{ "id": "../../evil", "displayName": "Evil" }""";
        string zip = SamplePets.Zip(_dir, "evil.zip", ("pet.json", SamplePets.Utf8(json)), ("spritesheet.webp", SamplePets.WebpHeader));

        PetPackage pet = Assert.IsType<PetPackage>(new PetLibrary(Library).Import(zip).Pet);

        Assert.Equal("evil", pet.Id);
        Assert.StartsWith(Library, pet.SpritesheetPath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1536, 1872, 9)]
    [InlineData(1536, 2288, 11)]
    [InlineData(768, 936, 9)]
    [InlineData(1536, 1536, null)]
    [InlineData(1537, 1872, null)]
    public void Pet_grids_are_recognised_by_size(int width, int height, int? rows) =>
        Assert.Equal(rows, SpritesheetGrid.RowsFor(width, height));

    [Fact]
    public void The_size_of_a_real_petdex_webp_is_read_from_its_header() =>
        Assert.Equal((1536, 1872), SpritesheetGrid.Size(SamplePets.WebpHeader));
}
