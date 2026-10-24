# Pennyworth Milestone 1: Storage Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A working Windows app that scans any drive without admin rights and shows, in plain language, where its used space goes (category summary that always adds up to 100%, biggest places per category, and a folder browser sorted largest first).

**Architecture:** A .NET 10 solution split into small libraries: `Core` (compact storage tree, contracts, formatting), `KnowledgeBase` (embedded bilingual JSON of known locations), `Insights` (path rules → categories), `Scanning` (parallel Win32 directory scanner) and `App` (Avalonia UI, MVVM, no business logic). The scanner reports live per-category totals while it runs; the UI binds to view models only.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), Avalonia 12.1.4 with `Avalonia.Themes.Simple` (custom look, no Windows theme), CommunityToolkit.Mvvm 8.4.2 (source generators), System.Text.Json source generation, xUnit v3 4.0.2 on Microsoft Testing Platform.

**Spec:** `docs/superpowers/specs/2026-10-24-pennyworth-v1-design.md`

## Global Constraints

- Repository root: `C:\Users\User\Documents\ws-code\Pennyworth` (all paths below are relative to it).
- .NET SDK `10.0.401` pinned in `global.json` with `rollForward: latestFeature`; every project targets `net10.0-windows`.
- Package versions live only in `Directory.Packages.props` (central package management); `.csproj` files never carry versions.
- `Nullable` enabled and `TreatWarningsAsErrors` true for every project.
- Every `src/` library sets `IsAotCompatible`; no reflection-based JSON (source-generated contexts only); Avalonia uses compiled bindings (`AvaloniaUseCompiledBindingsByDefault`) and every XAML template declares `x:DataType`.
- The UI process never runs elevated: `app.manifest` uses `asInvoker`.
- Windows 10 and 11 are supported.
- All user-visible text lives in `Resources/Strings.resx` (English) and `Resources/Strings.pt-BR.resx` (Portuguese, Brazil). Knowledge-base entries carry both `en` and `pt-BR`.
- Views reference only design-token keys from `Theme/Tokens.axaml` (no raw colours or sizes in views), so the later Claude Design work replaces tokens and styles without touching view models.
- Tests run with `dotnet test` (Microsoft Testing Platform via `global.json`). Run one project with `dotnet test --project tests/<Project>`.

## Roadmap (one plan per milestone)

The spec covers several independent subsystems, so it is delivered as milestones. Each later milestone gets its own plan, written when the previous one is done.

| Milestone | Delivers |
|---|---|
| **M1 (this plan)** | Solution foundation, knowledge base, classifier, normal (non-admin) scanner, storage summary, folder browser, EN/PT-BR strings, design tokens |
| M2: Fast scan and hidden space | Admin helper process + secured named pipe, `MftScanner`, hidden-system breakdown (shadow copies, reserved storage, NTFS metadata), "Unlock full scan" flow, scan snapshots for instant start, compact name storage in `StorageTree` |
| M3: Apps and games | Installed-app discovery (registry, Store packages, game launchers), true per-app size with confidence, app detail screen, official Uninstall |
| M4: Guided cleanup | Cleanup rules in the knowledge base, "You can free X GB" card, preview with safety badges, executor (direct delete / Recycle Bin / Windows mechanisms via helper), History |
| M5: Live RAM/CPU | Process snapshots, grouping by app, explained system processes, Close app |
| M6: Polish and release | Settings (language, theme, drives), first run, local error log, Native AOT publish, Velopack installer, CI, signing, performance checks |

## Findings from the prototype run (inform M1 and M2)

This plan's code was compiled and tested in a prototype, and the scanner was run against the real C: drive (954 GB SATA SSD, 1.7M files):

- A 512 GB **sparse** file (`AppData\Local\Google\Play Games\...\userdata.img`) made the first version count 1.34 TB on a drive with 873 GB used. M1 therefore asks Windows for the stored size of sparse and compressed files (Task 5). With that fix the same drive scans as 856 GB of 875 GB used, leaving 18.7 GB "not measured yet", which M2 breaks down.
- The first, cold-cache scan of all of C: took about 10 minutes, a later one 200 s; with a warm cache the scanner lists 200k files in ~7 s with 16 workers versus ~68 s with one. Directory listing is I/O bound, which is why M2's MFT scanner matters.
- Holding 1.7M nodes used ~284 MB of managed heap. Acceptable for M1; M2 compacts name storage.

## File Structure

```
global.json                          SDK pin + Microsoft Testing Platform runner
Directory.Build.props                Shared compiler settings
Directory.Packages.props             All package versions
.gitignore
Pennyworth.slnx                   Solution (XML format, .NET 10 default)
src/
  Pennyworth.Core/
    Formatting/ByteSize.cs           "12.3 GB" formatting (1024-based, culture aware)
    Storage/StorageCategory.cs       Category enum + display order
    Storage/StorageTreeBuilder.cs    Compact index-based tree builder (+ NodeData, NodeFlags)
    Storage/StorageTree.cs           Immutable result: totals, per-category bytes, category areas
    Storage/StorageNode.cs           Lightweight node handle: name, sizes, path, children
    Storage/IPathClassifier.cs       Path → category contract
    Storage/IStorageScanner.cs       Scanner contract
    Storage/ScanProgress.cs          Live progress record
  Pennyworth.KnowledgeBase/
    Data/locations.json              Known locations, EN + PT-BR (embedded resource)
    KnowledgeBaseModels.cs           LocalizedText, KnownLocation, KnowledgeBaseData, JSON context
    KnowledgeBaseLoader.cs           Loads the embedded or a given JSON
  Pennyworth.Insights/
    Classification/PathPattern.cs    ?: * ** path matching
    Classification/RuleClassifier.cs IPathClassifier over the knowledge base
  Pennyworth.Scanning/
    Native/NativeMethods.cs          FindFirstFileExW, GetCompressedFileSizeW, GetDiskFreeSpaceW
    Native/NativeDirectory.cs        Lists one directory; stored size; cluster size
    SizeOnDisk.cs                    Allocation estimate + which folders to enter
    DirectoryScanner.cs              Parallel non-admin scanner
  Pennyworth.App/
    Pennyworth.App.csproj, app.manifest, Program.cs, App.axaml(.cs)
    Resources/Strings.resx, Strings.pt-BR.resx
    Theme/Tokens.axaml, Theme/Styles.axaml
    Services/DriveService.cs, ExplorerLauncher.cs, LocationInfo.cs
    ViewModels/CategoryText.cs, StorageSummaryViewModel.cs, FolderBrowserViewModel.cs, MainWindowViewModel.cs
    Controls/SegmentedBar.cs
    Views/MainWindow.axaml(.cs), StorageSummaryView.axaml(.cs), FolderBrowserView.axaml(.cs)
tests/
  Pennyworth.Core.Tests/          ByteSizeTests, StorageTreeTests
  Pennyworth.KnowledgeBase.Tests/ KnowledgeBaseLoaderTests
  Pennyworth.Insights.Tests/      PathPatternTests, RuleClassifierTests
  Pennyworth.Scanning.Tests/      SizeOnDiskTests, DirectoryScannerTests
  Pennyworth.App.Tests/           Fakes, StringsTests, StorageSummaryViewModelTests, FolderBrowserViewModelTests, MainWindowViewModelTests
```

---

### Task 1: Solution scaffold and byte formatting

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`, `Pennyworth.slnx`
- Create: `src/Pennyworth.Core/Pennyworth.Core.csproj`, `src/Pennyworth.Core/Formatting/ByteSize.cs`
- Test: `tests/Pennyworth.Core.Tests/Pennyworth.Core.Tests.csproj`, `tests/Pennyworth.Core.Tests/ByteSizeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Pennyworth.Core.Formatting.ByteSize.Format(long bytes, IFormatProvider? provider = null) : string` (1024-based units B, KB, MB, GB, TB, PB; negative values format as `0 B`).

- [ ] **Step 1: Create the root build files**

`global.json`:

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestFeature"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Avalonia" Version="12.1.4" />
    <PackageVersion Include="Avalonia.Desktop" Version="12.1.4" />
    <PackageVersion Include="Avalonia.Themes.Simple" Version="12.1.4" />
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageVersion Include="xunit.v3" Version="4.0.2" />
  </ItemGroup>
</Project>
```

`.gitignore`:

```text
bin/
obj/
.vs/
*.user
TestResults/
```

- [ ] **Step 2: Create the solution and the Core projects**

Run:

```bash
dotnet new sln -n Pennyworth
```

Expected: `Pennyworth.slnx` is created (the .NET 10 SDK creates the XML solution format).

`src/Pennyworth.Core/Pennyworth.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
</Project>
```

`tests/Pennyworth.Core.Tests/Pennyworth.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
    <PackageReference Include="xunit.v3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Pennyworth.Core\Pennyworth.Core.csproj" />
  </ItemGroup>
</Project>
```

Run:

```bash
dotnet sln Pennyworth.slnx add src/Pennyworth.Core/Pennyworth.Core.csproj tests/Pennyworth.Core.Tests/Pennyworth.Core.Tests.csproj
```

- [ ] **Step 3: Write the failing test**

`tests/Pennyworth.Core.Tests/ByteSizeTests.cs`:

```csharp
using System.Globalization;
using Pennyworth.Core.Formatting;

namespace Pennyworth.Core.Tests;

public class ByteSizeTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(10L * 1024 * 1024 + 512 * 1024, "10.5 MB")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 GB")]
    [InlineData(-5L, "0 B")]
    public void Formats_with_binary_units(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes, En));
    }

    [Fact]
    public void Uses_culture_decimal_separator()
    {
        Assert.Equal("1,50 KB", ByteSize.Format(1536, CultureInfo.GetCultureInfo("pt-BR")));
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test --project tests/Pennyworth.Core.Tests`
Expected: build FAILS with `CS0234`/`CS0103`: the namespace `Pennyworth.Core.Formatting` / name `ByteSize` does not exist.

- [ ] **Step 5: Implement**

`src/Pennyworth.Core/Formatting/ByteSize.cs`:

```csharp
using System.Globalization;

namespace Pennyworth.Core.Formatting;

/// <summary>Formats byte counts the way Windows does (1 KB = 1024 bytes).</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        double value = Math.Max(bytes, 0);
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return $"{value.ToString(format, provider)} {Units[unit]}";
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project tests/Pennyworth.Core.Tests`
Expected: PASS, 7 tests.

- [ ] **Step 7: Commit**

```bash
git add global.json Directory.Build.props Directory.Packages.props .gitignore Pennyworth.slnx src/Pennyworth.Core tests/Pennyworth.Core.Tests
git commit -m "build: scaffold solution and add byte size formatting"
```

---

### Task 2: Storage tree model and scanner contracts

**Files:**
- Create: `src/Pennyworth.Core/Storage/StorageCategory.cs`, `StorageTreeBuilder.cs`, `StorageTree.cs`, `StorageNode.cs`, `IPathClassifier.cs`, `IStorageScanner.cs`, `ScanProgress.cs`
- Test: `tests/Pennyworth.Core.Tests/StorageTreeTests.cs`

**Interfaces:**
- Consumes: nothing beyond Task 1.
- Produces (namespace `Pennyworth.Core.Storage`):
  - `enum StorageCategory : byte { Other = 0, Games = 1, Apps = 2, YourFiles = 3, Windows = 4, HiddenSystem = 5 }`; `StorageCategories.All` (display order) and `StorageCategories.Count = 6`.
  - `StorageTreeBuilder(string rootPath, StorageCategory rootCategory)`; `const int RootIndex = 0`; `int AddDirectory(int parent, string name, StorageCategory category)`; `void AddFile(int parent, string name, long sizeOnDisk, long logicalSize, StorageCategory category)`; `void MarkLocked(int directory)`; `long FileCount`; `long TotalBytes`; `long[] SnapshotCategoryBytes()`; `StorageTree Build()` (once). Not thread-safe; parents before children.
  - `StorageTree`: `RootPath`, `FileCount`, `TotalBytes`, `Root : StorageNode`, `long GetCategoryBytes(StorageCategory)`, `IReadOnlyList<CategoryArea> GetCategoryAreas(StorageCategory)` (largest first).
  - `readonly record struct CategoryArea(StorageNode Node, long Bytes)`.
  - `readonly record struct StorageNode(StorageTree Tree, int Index)`: `Name`, `IsDirectory`, `IsLocked`, `SizeOnDisk`, `LogicalSize` (directories: sum of everything below), `Category`, `StorageNode? Parent`, `HasChildren`, `FullPath`, `IReadOnlyList<StorageNode> GetChildren()` (largest first).
  - `interface IPathClassifier { StorageCategory Classify(string fullPath, StorageCategory inherited); }`: called for every directory and for files directly in the scan root.
  - `interface IStorageScanner { Task<StorageTree> ScanAsync(string rootPath, IPathClassifier classifier, IProgress<ScanProgress>? progress, CancellationToken cancellationToken); }`
  - `sealed record ScanProgress(long FileCount, long TotalBytes, IReadOnlyList<long> CategoryBytes)` (indexed by `(int)StorageCategory`).

- [ ] **Step 1: Write the failing test**

`tests/Pennyworth.Core.Tests/StorageTreeTests.cs`:

```csharp
using Pennyworth.Core.Storage;

namespace Pennyworth.Core.Tests;

public class StorageTreeTests
{
    // C:\
    //   Windows\            (Windows)
    //     a.dll 100
    //   Users\              (Other)
    //     Bob\              (YourFiles)
    //       photo.jpg 300
    //       AppData\        (Apps)
    //         cache.bin 50
    //   hiberfil.sys 1000   (HiddenSystem, root file)
    private static StorageTree BuildSample()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        int windows = b.AddDirectory(StorageTreeBuilder.RootIndex, "Windows", StorageCategory.Windows);
        b.AddFile(windows, "a.dll", 100, 90, StorageCategory.Windows);
        int users = b.AddDirectory(StorageTreeBuilder.RootIndex, "Users", StorageCategory.Other);
        int bob = b.AddDirectory(users, "Bob", StorageCategory.YourFiles);
        b.AddFile(bob, "photo.jpg", 300, 290, StorageCategory.YourFiles);
        int appData = b.AddDirectory(bob, "AppData", StorageCategory.Apps);
        b.AddFile(appData, "cache.bin", 50, 40, StorageCategory.Apps);
        b.AddFile(StorageTreeBuilder.RootIndex, "hiberfil.sys", 1000, 1000, StorageCategory.HiddenSystem);
        return b.Build();
    }

    [Fact]
    public void Rolls_sizes_up_to_every_directory()
    {
        var tree = BuildSample();

        Assert.Equal(1450, tree.Root.SizeOnDisk);
        Assert.Equal(1420, tree.Root.LogicalSize);
        Assert.Equal(1450, tree.TotalBytes);
        Assert.Equal(4, tree.FileCount);
        var users = tree.Root.GetChildren().Single(n => n.Name == "Users");
        Assert.Equal(350, users.SizeOnDisk);
    }

    [Fact]
    public void Children_are_sorted_largest_first()
    {
        var names = BuildSample().Root.GetChildren().Select(n => n.Name);

        Assert.Equal(["hiberfil.sys", "Users", "Windows"], names);
    }

    [Fact]
    public void Builds_full_paths_from_the_root()
    {
        var bob = BuildSample().Root.GetChildren().Single(n => n.Name == "Users").GetChildren().Single();

        Assert.Equal(@"C:\Users\Bob", bob.FullPath);
        Assert.Equal(@"C:\", bob.Parent!.Value.Parent!.Value.FullPath);
    }

    [Fact]
    public void Tracks_bytes_per_category()
    {
        var tree = BuildSample();

        Assert.Equal(100, tree.GetCategoryBytes(StorageCategory.Windows));
        Assert.Equal(300, tree.GetCategoryBytes(StorageCategory.YourFiles));
        Assert.Equal(50, tree.GetCategoryBytes(StorageCategory.Apps));
        Assert.Equal(1000, tree.GetCategoryBytes(StorageCategory.HiddenSystem));
        Assert.Equal(0, tree.GetCategoryBytes(StorageCategory.Games));
    }

    [Fact]
    public void Category_areas_exclude_nested_areas_of_other_categories()
    {
        var tree = BuildSample();

        var yourFiles = Assert.Single(tree.GetCategoryAreas(StorageCategory.YourFiles));
        Assert.Equal(@"C:\Users\Bob", yourFiles.Node.FullPath);
        Assert.Equal(300, yourFiles.Bytes);

        var apps = Assert.Single(tree.GetCategoryAreas(StorageCategory.Apps));
        Assert.Equal(@"C:\Users\Bob\AppData", apps.Node.FullPath);

        var hidden = Assert.Single(tree.GetCategoryAreas(StorageCategory.HiddenSystem));
        Assert.Equal(@"C:\hiberfil.sys", hidden.Node.FullPath);
        Assert.False(hidden.Node.IsDirectory);

        Assert.Empty(tree.GetCategoryAreas(StorageCategory.Games));
    }

    [Fact]
    public void Marks_locked_directories()
    {
        var b = new StorageTreeBuilder(@"D:\", StorageCategory.Other);
        int secret = b.AddDirectory(StorageTreeBuilder.RootIndex, "Secret", StorageCategory.Other);
        b.MarkLocked(secret);

        var node = Assert.Single(b.Build().Root.GetChildren());

        Assert.True(node.IsLocked);
        Assert.False(node.HasChildren);
    }

    [Fact]
    public void Rejects_children_added_to_a_file()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        b.AddFile(StorageTreeBuilder.RootIndex, "a.txt", 1, 1, StorageCategory.Other);

        Assert.Throws<ArgumentOutOfRangeException>(() => b.AddFile(1, "b.txt", 1, 1, StorageCategory.Other));
    }

    [Fact]
    public void Snapshot_reflects_files_added_so_far()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        b.AddFile(StorageTreeBuilder.RootIndex, "a.bin", 10, 10, StorageCategory.Games);

        var snapshot = b.SnapshotCategoryBytes();

        Assert.Equal(10, snapshot[(int)StorageCategory.Games]);
        Assert.Equal(StorageCategories.Count, snapshot.Length);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Pennyworth.Core.Tests`
Expected: build FAILS: `The type or namespace name 'Storage' does not exist in the namespace 'Pennyworth.Core'`.

- [ ] **Step 3: Implement the categories and contracts**

`src/Pennyworth.Core/Storage/StorageCategory.cs`:

```csharp
namespace Pennyworth.Core.Storage;

/// <summary>Every scanned byte belongs to exactly one category.</summary>
public enum StorageCategory : byte
{
    Other = 0,
    Games = 1,
    Apps = 2,
    YourFiles = 3,
    Windows = 4,
    HiddenSystem = 5,
}

public static class StorageCategories
{
    /// <summary>Display order used by the summary bar.</summary>
    public static readonly IReadOnlyList<StorageCategory> All =
    [
        StorageCategory.Games,
        StorageCategory.Apps,
        StorageCategory.YourFiles,
        StorageCategory.Windows,
        StorageCategory.HiddenSystem,
        StorageCategory.Other,
    ];

    public const int Count = 6;
}
```

`src/Pennyworth.Core/Storage/IPathClassifier.cs`:

```csharp
namespace Pennyworth.Core.Storage;

public interface IPathClassifier
{
    /// <summary>
    /// Returns the category for a directory, or for a file directly in the drive root.
    /// Returns <paramref name="inherited"/> (the parent's category) when no rule matches.
    /// </summary>
    StorageCategory Classify(string fullPath, StorageCategory inherited);
}
```

`src/Pennyworth.Core/Storage/ScanProgress.cs`:

```csharp
namespace Pennyworth.Core.Storage;

/// <param name="CategoryBytes">Bytes per category, indexed by <c>(int)StorageCategory</c>.</param>
public sealed record ScanProgress(long FileCount, long TotalBytes, IReadOnlyList<long> CategoryBytes);
```

`src/Pennyworth.Core/Storage/IStorageScanner.cs`:

```csharp
namespace Pennyworth.Core.Storage;

public interface IStorageScanner
{
    Task<StorageTree> ScanAsync(
        string rootPath,
        IPathClassifier classifier,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement the tree**

The tree stores nodes in flat arrays (parent / first child / next sibling indices) rather than one object per node, because a full drive holds millions of entries. Children always get a higher index than their parent, so `Build()` rolls sizes up in one reverse pass.

`src/Pennyworth.Core/Storage/StorageTreeBuilder.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Pennyworth.Core.Storage;

/// <summary>
/// Collects scan results into a compact, index-based tree. Not thread-safe:
/// scanners must serialize calls. Parents must be added before their children.
/// </summary>
public sealed class StorageTreeBuilder
{
    public const int RootIndex = 0;

    private readonly string _rootPath;
    private readonly List<NodeData> _nodes = [];
    private readonly List<string> _names = [];
    private readonly long[] _categoryBytes = new long[StorageCategories.Count];
    private bool _built;

    public StorageTreeBuilder(string rootPath, StorageCategory rootCategory)
    {
        _rootPath = rootPath;
        Add(-1, rootPath, rootCategory, NodeFlags.Directory, 0, 0);
    }

    public long FileCount { get; private set; }

    public long TotalBytes { get; private set; }

    public int AddDirectory(int parent, string name, StorageCategory category)
    {
        EnsureDirectory(parent);
        return Add(parent, name, category, NodeFlags.Directory, 0, 0);
    }

    public void AddFile(int parent, string name, long sizeOnDisk, long logicalSize, StorageCategory category)
    {
        EnsureDirectory(parent);
        Add(parent, name, category, NodeFlags.None, sizeOnDisk, logicalSize);
        FileCount++;
        TotalBytes += sizeOnDisk;
        _categoryBytes[(int)category] += sizeOnDisk;
    }

    /// <summary>Marks a directory whose contents could not be read.</summary>
    public void MarkLocked(int directory)
    {
        EnsureDirectory(directory);
        CollectionsMarshal.AsSpan(_nodes)[directory].Flags |= NodeFlags.Locked;
    }

    public long[] SnapshotCategoryBytes() => (long[])_categoryBytes.Clone();

    public StorageTree Build()
    {
        if (_built)
        {
            throw new InvalidOperationException("Build can only be called once.");
        }

        _built = true;
        NodeData[] nodes = [.. _nodes];

        // Children always have a higher index than their parent, so one reverse pass
        // rolls every size up to the root.
        for (int i = nodes.Length - 1; i > 0; i--)
        {
            ref NodeData parent = ref nodes[nodes[i].Parent];
            parent.SizeOnDisk += nodes[i].SizeOnDisk;
            parent.LogicalSize += nodes[i].LogicalSize;
        }

        return new StorageTree(_rootPath, nodes, [.. _names], FileCount, TotalBytes, SnapshotCategoryBytes());
    }

    private int Add(int parent, string name, StorageCategory category, NodeFlags flags, long sizeOnDisk, long logicalSize)
    {
        if (_built)
        {
            throw new InvalidOperationException("The tree has already been built.");
        }

        int index = _nodes.Count;
        var node = new NodeData
        {
            Parent = parent,
            FirstChild = -1,
            NextSibling = -1,
            SizeOnDisk = sizeOnDisk,
            LogicalSize = logicalSize,
            Category = category,
            Flags = flags,
        };

        if (parent >= 0)
        {
            ref NodeData parentNode = ref CollectionsMarshal.AsSpan(_nodes)[parent];
            node.NextSibling = parentNode.FirstChild;
            parentNode.FirstChild = index;
        }

        _nodes.Add(node);
        _names.Add(name);
        return index;
    }

    private void EnsureDirectory(int index)
    {
        if ((uint)index >= (uint)_nodes.Count || (_nodes[index].Flags & NodeFlags.Directory) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Not a directory in this tree.");
        }
    }
}

[Flags]
internal enum NodeFlags : byte
{
    None = 0,
    Directory = 1,
    Locked = 2,
}

internal struct NodeData
{
    public int Parent;
    public int FirstChild;
    public int NextSibling;
    public long SizeOnDisk;
    public long LogicalSize;
    public StorageCategory Category;
    public NodeFlags Flags;
}
```

`src/Pennyworth.Core/Storage/StorageTree.cs`:

```csharp
namespace Pennyworth.Core.Storage;

/// <summary>An immutable scan result. Directory sizes include everything below them.</summary>
public sealed class StorageTree
{
    private readonly NodeData[] _nodes;
    private readonly string[] _names;
    private readonly long[] _categoryBytes;
    private Dictionary<StorageCategory, IReadOnlyList<CategoryArea>>? _areas;

    internal StorageTree(string rootPath, NodeData[] nodes, string[] names, long fileCount, long totalBytes, long[] categoryBytes)
    {
        RootPath = rootPath;
        _nodes = nodes;
        _names = names;
        FileCount = fileCount;
        TotalBytes = totalBytes;
        _categoryBytes = categoryBytes;
    }

    public string RootPath { get; }

    public long FileCount { get; }

    public long TotalBytes { get; }

    public StorageNode Root => new(this, StorageTreeBuilder.RootIndex);

    public long GetCategoryBytes(StorageCategory category) => _categoryBytes[(int)category];

    /// <summary>
    /// The top-most folders (or root files) where a category starts, each with only the
    /// bytes below it that belong to that category. Largest first.
    /// </summary>
    public IReadOnlyList<CategoryArea> GetCategoryAreas(StorageCategory category)
    {
        _areas ??= ComputeAreas();
        return _areas.TryGetValue(category, out var list) ? list : [];
    }

    internal ref readonly NodeData Node(int index) => ref _nodes[index];

    internal string Name(int index) => _names[index];

    private Dictionary<StorageCategory, IReadOnlyList<CategoryArea>> ComputeAreas()
    {
        // A node starts a new area when its category differs from its parent's.
        var areaOf = new int[_nodes.Length];
        var bytesByArea = new Dictionary<int, long>();
        for (int i = 0; i < _nodes.Length; i++)
        {
            ref readonly NodeData node = ref _nodes[i];
            areaOf[i] = i == 0 || node.Category != _nodes[node.Parent].Category ? i : areaOf[node.Parent];
            if ((node.Flags & NodeFlags.Directory) == 0)
            {
                bytesByArea[areaOf[i]] = bytesByArea.GetValueOrDefault(areaOf[i]) + node.SizeOnDisk;
            }
        }

        return bytesByArea
            .Where(pair => pair.Value > 0)
            .GroupBy(pair => _nodes[pair.Key].Category)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CategoryArea>)group
                    .Select(pair => new CategoryArea(new StorageNode(this, pair.Key), pair.Value))
                    .OrderByDescending(area => area.Bytes)
                    .ToArray());
    }
}

public readonly record struct CategoryArea(StorageNode Node, long Bytes);
```

`src/Pennyworth.Core/Storage/StorageNode.cs`:

```csharp
namespace Pennyworth.Core.Storage;

/// <summary>A lightweight handle to one file or folder in a <see cref="StorageTree"/>.</summary>
public readonly record struct StorageNode(StorageTree Tree, int Index)
{
    public string Name => Tree.Name(Index);

    public bool IsDirectory => (Tree.Node(Index).Flags & NodeFlags.Directory) != 0;

    /// <summary>True when Windows refused to list this folder's contents.</summary>
    public bool IsLocked => (Tree.Node(Index).Flags & NodeFlags.Locked) != 0;

    public long SizeOnDisk => Tree.Node(Index).SizeOnDisk;

    public long LogicalSize => Tree.Node(Index).LogicalSize;

    public StorageCategory Category => Tree.Node(Index).Category;

    public StorageNode? Parent =>
        Index == StorageTreeBuilder.RootIndex ? null : new StorageNode(Tree, Tree.Node(Index).Parent);

    public bool HasChildren => Tree.Node(Index).FirstChild >= 0;

    public string FullPath
    {
        get
        {
            var segments = new Stack<string>();
            for (StorageNode? node = this; node is { } current; node = current.Parent)
            {
                segments.Push(current.Name);
            }

            return Path.Combine([.. segments]);
        }
    }

    /// <summary>Direct children, largest first.</summary>
    public IReadOnlyList<StorageNode> GetChildren()
    {
        var children = new List<StorageNode>();
        for (int child = Tree.Node(Index).FirstChild; child >= 0; child = Tree.Node(child).NextSibling)
        {
            children.Add(new StorageNode(Tree, child));
        }

        children.Sort((a, b) => b.SizeOnDisk.CompareTo(a.SizeOnDisk));
        return children;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project tests/Pennyworth.Core.Tests`
Expected: PASS, 15 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Pennyworth.Core tests/Pennyworth.Core.Tests
git commit -m "feat(core): add compact storage tree with category areas"
```

---

### Task 3: Knowledge base of known locations

**Files:**
- Create: `src/Pennyworth.KnowledgeBase/Pennyworth.KnowledgeBase.csproj`, `KnowledgeBaseModels.cs`, `KnowledgeBaseLoader.cs`, `Data/locations.json`
- Test: `tests/Pennyworth.KnowledgeBase.Tests/Pennyworth.KnowledgeBase.Tests.csproj`, `KnowledgeBaseLoaderTests.cs`

**Interfaces:**
- Consumes: `StorageCategory` (Task 2).
- Produces (namespace `Pennyworth.KnowledgeBase`):
  - `sealed record LocalizedText(string En, string PtBr)` with `string Get(CultureInfo culture)` (any `pt*` culture → `PtBr`); JSON keys `en` and `pt-BR`.
  - `sealed record KnownLocation(string Id, string Path, StorageCategory Category, LocalizedText Title, LocalizedText Description)`.
  - `sealed record KnowledgeBaseData(int Version, IReadOnlyList<KnownLocation> Locations)`.
  - `static class KnowledgeBaseLoader { KnowledgeBaseData LoadEmbedded(); KnowledgeBaseData Parse(string json); }`.
- `Path` syntax (consumed by Task 4): `%ENV%` variables, `?:` = any drive, `*` = one folder name, leading `**` = any prefix. The category applies to the match and everything below it.

- [ ] **Step 1: Create the projects**

`src/Pennyworth.KnowledgeBase/Pennyworth.KnowledgeBase.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="Data\locations.json" LogicalName="Pennyworth.KnowledgeBase.locations.json" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Pennyworth.Core\Pennyworth.Core.csproj" />
  </ItemGroup>
</Project>
```

`tests/Pennyworth.KnowledgeBase.Tests/Pennyworth.KnowledgeBase.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
    <PackageReference Include="xunit.v3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Pennyworth.KnowledgeBase\Pennyworth.KnowledgeBase.csproj" />
  </ItemGroup>
</Project>
```

Run:

```bash
dotnet sln Pennyworth.slnx add src/Pennyworth.KnowledgeBase/Pennyworth.KnowledgeBase.csproj tests/Pennyworth.KnowledgeBase.Tests/Pennyworth.KnowledgeBase.Tests.csproj
```

- [ ] **Step 2: Write the failing test**

`tests/Pennyworth.KnowledgeBase.Tests/KnowledgeBaseLoaderTests.cs`:

```csharp
using System.Globalization;
using Pennyworth.Core.Storage;

namespace Pennyworth.KnowledgeBase.Tests;

public class KnowledgeBaseLoaderTests
{
    [Fact]
    public void Parses_locations_with_both_languages()
    {
        const string json = """
            {
              "version": 1,
              "locations": [
                {
                  "id": "windows",
                  "path": "%SystemRoot%",
                  "category": "Windows",
                  "title": { "en": "Windows", "pt-BR": "Janelas" },
                  "description": { "en": "System", "pt-BR": "Sistema" }
                }
              ]
            }
            """;

        var data = KnowledgeBaseLoader.Parse(json);

        var location = Assert.Single(data.Locations);
        Assert.Equal("windows", location.Id);
        Assert.Equal(StorageCategory.Windows, location.Category);
        Assert.Equal("Windows", location.Title.Get(CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("Janelas", location.Title.Get(CultureInfo.GetCultureInfo("pt-BR")));
    }

    [Fact]
    public void Embedded_knowledge_base_is_complete_and_unique()
    {
        var data = KnowledgeBaseLoader.LoadEmbedded();

        Assert.NotEmpty(data.Locations);
        Assert.Equal(data.Locations.Count, data.Locations.Select(l => l.Id).Distinct().Count());
        Assert.All(data.Locations, location =>
        {
            Assert.False(string.IsNullOrWhiteSpace(location.Path));
            Assert.False(string.IsNullOrWhiteSpace(location.Title.En));
            Assert.False(string.IsNullOrWhiteSpace(location.Title.PtBr));
            Assert.False(string.IsNullOrWhiteSpace(location.Description.En));
            Assert.False(string.IsNullOrWhiteSpace(location.Description.PtBr));
        });
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --project tests/Pennyworth.KnowledgeBase.Tests`
Expected: build FAILS: `KnowledgeBaseLoader` does not exist (and the `Data\locations.json` embedded resource is missing).

- [ ] **Step 4: Implement the models and loader**

`src/Pennyworth.KnowledgeBase/KnowledgeBaseModels.cs`:

```csharp
using System.Globalization;
using System.Text.Json.Serialization;
using Pennyworth.Core.Storage;

namespace Pennyworth.KnowledgeBase;

public sealed record LocalizedText(
    string En,
    [property: JsonPropertyName("pt-BR")] string PtBr)
{
    public string Get(CultureInfo culture) =>
        culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? PtBr : En;
}

/// <summary>
/// A known folder or root file. <see cref="Path"/> supports <c>%ENV%</c> variables,
/// <c>?:</c> for any drive, <c>*</c> for one folder name and a leading <c>**</c> for any prefix.
/// The category applies to the matched item and everything below it.
/// </summary>
public sealed record KnownLocation(
    string Id,
    string Path,
    StorageCategory Category,
    LocalizedText Title,
    LocalizedText Description);

public sealed record KnowledgeBaseData(int Version, IReadOnlyList<KnownLocation> Locations);

[JsonSerializable(typeof(KnowledgeBaseData))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
internal sealed partial class KnowledgeBaseJsonContext : JsonSerializerContext;
```

`src/Pennyworth.KnowledgeBase/KnowledgeBaseLoader.cs`:

```csharp
using System.Text.Json;

namespace Pennyworth.KnowledgeBase;

public static class KnowledgeBaseLoader
{
    private const string ResourceName = "Pennyworth.KnowledgeBase.locations.json";

    public static KnowledgeBaseData LoadEmbedded()
    {
        using Stream stream = typeof(KnowledgeBaseLoader).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        return JsonSerializer.Deserialize(stream, KnowledgeBaseJsonContext.Default.KnowledgeBaseData)
            ?? throw new InvalidDataException("The knowledge base is empty.");
    }

    public static KnowledgeBaseData Parse(string json) =>
        JsonSerializer.Deserialize(json, KnowledgeBaseJsonContext.Default.KnowledgeBaseData)
        ?? throw new InvalidDataException("The knowledge base is empty.");
}
```

- [ ] **Step 5: Add the knowledge base data**

`src/Pennyworth.KnowledgeBase/Data/locations.json`:

```json
{
  "version": 1,
  "locations": [
    {
      "id": "windows",
      "path": "%SystemRoot%",
      "category": "Windows",
      "title": { "en": "Windows system files", "pt-BR": "Arquivos do sistema Windows" },
      "description": {
        "en": "The files Windows needs to run. Never delete anything here by hand.",
        "pt-BR": "Os arquivos que o Windows precisa para funcionar. Nunca apague nada aqui manualmente."
      }
    },
    {
      "id": "windows-old",
      "path": "%SystemDrive%\\Windows.old",
      "category": "Windows",
      "title": { "en": "Previous Windows installation", "pt-BR": "Instalação anterior do Windows" },
      "description": {
        "en": "A copy of your old Windows kept after an upgrade, so you can go back. Windows removes it after about 10 days.",
        "pt-BR": "Uma cópia do Windows antigo guardada após uma atualização, para você poder voltar. O Windows a remove após cerca de 10 dias."
      }
    },
    {
      "id": "windows-upgrade-files",
      "path": "%SystemDrive%\\$WINDOWS.~BT",
      "category": "Windows",
      "title": { "en": "Windows upgrade files", "pt-BR": "Arquivos de atualização do Windows" },
      "description": {
        "en": "Temporary files used while installing a Windows upgrade.",
        "pt-BR": "Arquivos temporários usados durante a instalação de uma atualização do Windows."
      }
    },
    {
      "id": "program-files",
      "path": "?:\\Program Files",
      "category": "Apps",
      "title": { "en": "Installed programs", "pt-BR": "Programas instalados" },
      "description": {
        "en": "Where most programs are installed. Remove programs with their uninstaller, not by deleting folders.",
        "pt-BR": "Onde a maioria dos programas é instalada. Remova programas pelo desinstalador, não apagando pastas."
      }
    },
    {
      "id": "program-files-x86",
      "path": "?:\\Program Files (x86)",
      "category": "Apps",
      "title": { "en": "Installed programs (32-bit)", "pt-BR": "Programas instalados (32 bits)" },
      "description": {
        "en": "Where older 32-bit programs are installed.",
        "pt-BR": "Onde programas mais antigos de 32 bits são instalados."
      }
    },
    {
      "id": "program-data",
      "path": "%ProgramData%",
      "category": "Apps",
      "title": { "en": "Shared app data", "pt-BR": "Dados compartilhados de apps" },
      "description": {
        "en": "Settings and data that programs share between all users of this PC.",
        "pt-BR": "Configurações e dados que os programas compartilham entre todos os usuários deste PC."
      }
    },
    {
      "id": "user-profile",
      "path": "%SystemDrive%\\Users\\*",
      "category": "YourFiles",
      "title": { "en": "User folder", "pt-BR": "Pasta do usuário" },
      "description": {
        "en": "Documents, pictures, downloads, desktop and other personal files of one user.",
        "pt-BR": "Documentos, imagens, downloads, área de trabalho e outros arquivos pessoais de um usuário."
      }
    },
    {
      "id": "user-appdata",
      "path": "%SystemDrive%\\Users\\*\\AppData",
      "category": "Apps",
      "title": { "en": "App data (AppData)", "pt-BR": "Dados de apps (AppData)" },
      "description": {
        "en": "A hidden folder where apps keep settings, caches and downloads for one user. Often much bigger than people expect.",
        "pt-BR": "Uma pasta oculta onde os apps guardam configurações, caches e downloads de um usuário. Muitas vezes bem maior do que se imagina."
      }
    },
    {
      "id": "recycle-bin",
      "path": "?:\\$Recycle.Bin",
      "category": "YourFiles",
      "title": { "en": "Recycle Bin", "pt-BR": "Lixeira" },
      "description": {
        "en": "Files you deleted. They still use space until the Recycle Bin is emptied.",
        "pt-BR": "Arquivos que você apagou. Eles continuam ocupando espaço até a Lixeira ser esvaziada."
      }
    },
    {
      "id": "steam-library",
      "path": "**\\steamapps",
      "category": "Games",
      "title": { "en": "Steam games", "pt-BR": "Jogos da Steam" },
      "description": {
        "en": "Games, mods and downloads from Steam.",
        "pt-BR": "Jogos, mods e downloads da Steam."
      }
    },
    {
      "id": "epic-games",
      "path": "?:\\Program Files\\Epic Games",
      "category": "Games",
      "title": { "en": "Epic Games", "pt-BR": "Epic Games" },
      "description": {
        "en": "Games installed by the Epic Games Launcher.",
        "pt-BR": "Jogos instalados pela Epic Games Launcher."
      }
    },
    {
      "id": "xbox-games",
      "path": "?:\\XboxGames",
      "category": "Games",
      "title": { "en": "Xbox / Game Pass games", "pt-BR": "Jogos do Xbox / Game Pass" },
      "description": {
        "en": "Games installed by the Xbox app.",
        "pt-BR": "Jogos instalados pelo app Xbox."
      }
    },
    {
      "id": "riot-games",
      "path": "?:\\Riot Games",
      "category": "Games",
      "title": { "en": "Riot Games", "pt-BR": "Riot Games" },
      "description": {
        "en": "Games from Riot, such as League of Legends and Valorant.",
        "pt-BR": "Jogos da Riot, como League of Legends e Valorant."
      }
    },
    {
      "id": "system-volume-information",
      "path": "?:\\System Volume Information",
      "category": "HiddenSystem",
      "title": { "en": "Restore points and system data", "pt-BR": "Pontos de restauração e dados do sistema" },
      "description": {
        "en": "Protected folder with restore points and indexing data. Its real size needs the full scan.",
        "pt-BR": "Pasta protegida com pontos de restauração e dados de indexação. O tamanho real precisa da verificação completa."
      }
    },
    {
      "id": "recovery",
      "path": "%SystemDrive%\\Recovery",
      "category": "HiddenSystem",
      "title": { "en": "Recovery tools", "pt-BR": "Ferramentas de recuperação" },
      "description": {
        "en": "Tools Windows uses to repair itself when it can't start.",
        "pt-BR": "Ferramentas que o Windows usa para se reparar quando não consegue iniciar."
      }
    },
    {
      "id": "hibernation-file",
      "path": "%SystemDrive%\\hiberfil.sys",
      "category": "HiddenSystem",
      "title": { "en": "Hibernation file", "pt-BR": "Arquivo de hibernação" },
      "description": {
        "en": "Saves your open apps when the PC hibernates. Usually 40% or more of your RAM size.",
        "pt-BR": "Guarda seus apps abertos quando o PC hiberna. Normalmente 40% ou mais do tamanho da sua RAM."
      }
    },
    {
      "id": "page-file",
      "path": "?:\\pagefile.sys",
      "category": "HiddenSystem",
      "title": { "en": "Page file (virtual memory)", "pt-BR": "Arquivo de paginação (memória virtual)" },
      "description": {
        "en": "Extra memory on disk Windows uses when RAM is full.",
        "pt-BR": "Memória extra no disco que o Windows usa quando a RAM está cheia."
      }
    },
    {
      "id": "swap-file",
      "path": "?:\\swapfile.sys",
      "category": "HiddenSystem",
      "title": { "en": "Swap file", "pt-BR": "Arquivo de troca" },
      "description": {
        "en": "Memory on disk used by Store apps.",
        "pt-BR": "Memória no disco usada pelos apps da Store."
      }
    }
  ]
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project tests/Pennyworth.KnowledgeBase.Tests`
Expected: PASS, 2 tests.

- [ ] **Step 7: Commit**

```bash
git add Pennyworth.slnx src/Pennyworth.KnowledgeBase tests/Pennyworth.KnowledgeBase.Tests
git commit -m "feat(kb): add bilingual knowledge base of known locations"
```

---

### Task 4: Path rules and the category classifier

**Files:**
- Create: `src/Pennyworth.Insights/Pennyworth.Insights.csproj`, `Classification/PathPattern.cs`, `Classification/RuleClassifier.cs`
- Test: `tests/Pennyworth.Insights.Tests/Pennyworth.Insights.Tests.csproj`, `PathPatternTests.cs`, `RuleClassifierTests.cs`

**Interfaces:**
- Consumes: `IPathClassifier`, `StorageCategory` (Task 2); `KnownLocation`, `KnowledgeBaseLoader` (Task 3).
- Produces (namespace `Pennyworth.Insights.Classification`):
  - `PathPattern.Parse(string pattern) : PathPattern` (throws `FormatException` for empty patterns, a lone `**`, or `**` anywhere but first); `static string[] SplitPath(string path)`; `bool IsMatch(string[] pathSegments)`; `int Specificity`.
  - `RuleClassifier(IEnumerable<KnownLocation> locations, Func<string, string?>? getEnvironmentVariable = null) : IPathClassifier`; `KnownLocation? FindLocation(string fullPath)` (most specific exact match); rules whose `%VAR%` is undefined are skipped.

- [ ] **Step 1: Create the projects**

`src/Pennyworth.Insights/Pennyworth.Insights.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Pennyworth.Insights.Tests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Pennyworth.Core\Pennyworth.Core.csproj" />
    <ProjectReference Include="..\Pennyworth.KnowledgeBase\Pennyworth.KnowledgeBase.csproj" />
  </ItemGroup>
</Project>
```

`tests/Pennyworth.Insights.Tests/Pennyworth.Insights.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
    <PackageReference Include="xunit.v3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Pennyworth.Insights\Pennyworth.Insights.csproj" />
  </ItemGroup>
</Project>
```

Run:

```bash
dotnet sln Pennyworth.slnx add src/Pennyworth.Insights/Pennyworth.Insights.csproj tests/Pennyworth.Insights.Tests/Pennyworth.Insights.Tests.csproj
```

- [ ] **Step 2: Write the failing tests**

`tests/Pennyworth.Insights.Tests/PathPatternTests.cs`:

```csharp
using Pennyworth.Insights.Classification;

namespace Pennyworth.Insights.Tests;

public class PathPatternTests
{
    [Theory]
    [InlineData(@"C:\Windows", @"C:\Windows", true)]
    [InlineData(@"C:\Windows", @"c:\WINDOWS", true)]
    [InlineData(@"C:\Windows", @"C:\Windows\System32", false)]
    [InlineData(@"C:\Users\*", @"C:\Users\Bob", true)]
    [InlineData(@"C:\Users\*", @"C:\Users", false)]
    [InlineData(@"?:\pagefile.sys", @"D:\pagefile.sys", true)]
    [InlineData(@"?:\pagefile.sys", @"D:\Games\pagefile.sys", false)]
    [InlineData(@"**\steamapps", @"D:\SteamLibrary\steamapps", true)]
    [InlineData(@"**\steamapps", @"C:\Program Files (x86)\Steam\steamapps", true)]
    [InlineData(@"**\steamapps", @"C:\steamapps\common", false)]
    public void Matches_paths(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathPattern.Parse(pattern).IsMatch(PathPattern.SplitPath(path)));
    }

    [Fact]
    public void Exact_patterns_are_more_specific_than_wildcard_prefixes()
    {
        Assert.True(PathPattern.Parse(@"C:\Users\*\AppData").Specificity > PathPattern.Parse(@"C:\Users\*").Specificity);
        Assert.True(PathPattern.Parse(@"C:\steamapps").Specificity > PathPattern.Parse(@"**\steamapps").Specificity);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"**")]
    [InlineData(@"C:\**\x")]
    public void Rejects_invalid_patterns(string pattern)
    {
        Assert.Throws<FormatException>(() => PathPattern.Parse(pattern));
    }
}
```

`tests/Pennyworth.Insights.Tests/RuleClassifierTests.cs`:

```csharp
using Pennyworth.Core.Storage;
using Pennyworth.Insights.Classification;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Insights.Tests;

public class RuleClassifierTests
{
    private static readonly Dictionary<string, string> Environment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["SystemDrive"] = "C:",
        ["ProgramData"] = @"C:\ProgramData",
    };

    private static RuleClassifier CreateWithEmbeddedRules() =>
        new(KnowledgeBaseLoader.LoadEmbedded().Locations, name => Environment.GetValueOrDefault(name));

    [Theory]
    [InlineData(@"C:\Windows", StorageCategory.Windows)]
    [InlineData(@"C:\Program Files", StorageCategory.Apps)]
    [InlineData(@"D:\Program Files (x86)", StorageCategory.Apps)]
    [InlineData(@"C:\Users\Bob", StorageCategory.YourFiles)]
    [InlineData(@"C:\Users\Bob\AppData", StorageCategory.Apps)]
    [InlineData(@"D:\SteamLibrary\steamapps", StorageCategory.Games)]
    [InlineData(@"C:\Program Files\Epic Games", StorageCategory.Games)]
    [InlineData(@"C:\hiberfil.sys", StorageCategory.HiddenSystem)]
    [InlineData(@"E:\pagefile.sys", StorageCategory.HiddenSystem)]
    [InlineData(@"C:\System Volume Information", StorageCategory.HiddenSystem)]
    [InlineData(@"C:\$Recycle.Bin", StorageCategory.YourFiles)]
    public void Classifies_known_locations(string path, StorageCategory expected)
    {
        Assert.Equal(expected, CreateWithEmbeddedRules().Classify(path, StorageCategory.Other));
    }

    [Fact]
    public void Unknown_paths_keep_the_inherited_category()
    {
        var classifier = CreateWithEmbeddedRules();

        Assert.Equal(StorageCategory.Windows, classifier.Classify(@"C:\Windows\System32", StorageCategory.Windows));
        Assert.Equal(StorageCategory.Other, classifier.Classify(@"D:\Stuff", StorageCategory.Other));
    }

    [Fact]
    public void Finds_the_location_for_friendly_titles()
    {
        var location = CreateWithEmbeddedRules().FindLocation(@"C:\Users\Bob\AppData");

        Assert.Equal("user-appdata", location?.Id);
    }

    [Fact]
    public void Skips_rules_whose_environment_variable_is_missing()
    {
        var classifier = new RuleClassifier(KnowledgeBaseLoader.LoadEmbedded().Locations, _ => null);

        Assert.Equal(StorageCategory.Other, classifier.Classify(@"C:\Windows", StorageCategory.Other));
        Assert.Equal(StorageCategory.Apps, classifier.Classify(@"C:\Program Files", StorageCategory.Other));
    }

    [Theory]
    [InlineData(@"%SystemRoot%\Temp", @"C:\Windows\Temp")]
    [InlineData(@"%SystemDrive%\Users\*", @"C:\Users\*")]
    [InlineData(@"?:\pagefile.sys", @"?:\pagefile.sys")]
    public void Expands_environment_variables(string path, string expected)
    {
        Assert.Equal(expected, RuleClassifier.ExpandVariables(path, name => Environment.GetValueOrDefault(name)));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --project tests/Pennyworth.Insights.Tests`
Expected: build FAILS: namespace `Pennyworth.Insights.Classification` does not exist.

- [ ] **Step 4: Implement**

`src/Pennyworth.Insights/Classification/PathPattern.cs`:

```csharp
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
```

`src/Pennyworth.Insights/Classification/RuleClassifier.cs`:

```csharp
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project tests/Pennyworth.Insights.Tests`
Expected: PASS, 31 tests.

- [ ] **Step 6: Commit**

```bash
git add Pennyworth.slnx src/Pennyworth.Insights tests/Pennyworth.Insights.Tests
git commit -m "feat(insights): classify folders into categories from known locations"
```

---

### Task 5: Native directory listing and size-on-disk rules

**Files:**
- Create: `src/Pennyworth.Scanning/Pennyworth.Scanning.csproj`, `Native/NativeMethods.cs`, `Native/NativeDirectory.cs`, `SizeOnDisk.cs`
- Test: `tests/Pennyworth.Scanning.Tests/Pennyworth.Scanning.Tests.csproj`, `SizeOnDiskTests.cs`

**Interfaces:**
- Consumes: nothing beyond Core.
- Produces (internal to `Pennyworth.Scanning`, visible to its tests):
  - `readonly record struct DirectoryEntry(string Name, FileAttributes Attributes, long Size, uint ReparseTag)` with `IsDirectory`.
  - `NativeDirectory.TryList(string directoryPath, List<DirectoryEntry> entries) : bool` (false = access denied / vanished → caller marks locked); `NativeDirectory.GetStoredSize(string filePath) : long?`; `NativeDirectory.GetClusterSize(string rootPath) : long` (falls back to 4096).
  - `SizeOnDisk.NeedsStoredSize(FileAttributes) : bool`; `SizeOnDisk.Estimate(FileAttributes attributes, long logicalSize, long clusterSize, long? storedSize = null) : long`; `SizeOnDisk.ShouldEnterDirectory(FileAttributes attributes, uint reparseTag) : bool`.

Why these rules: a directory listing only gives logical sizes. Rounding to clusters approximates allocation; OneDrive "online-only" files use no space; sparse/compressed files (WSL, Docker and emulator disks) must use the stored size or the totals explode (see the prototype finding); junctions and symbolic links are skipped so nothing is counted twice, but cloud-sync folders (reparse tag `0x9000xxxA`) are real and entered.

- [ ] **Step 1: Create the projects**

`src/Pennyworth.Scanning/Pennyworth.Scanning.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Pennyworth.Scanning.Tests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Pennyworth.Core\Pennyworth.Core.csproj" />
  </ItemGroup>
</Project>
```

`tests/Pennyworth.Scanning.Tests/Pennyworth.Scanning.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
    <PackageReference Include="xunit.v3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Pennyworth.Scanning\Pennyworth.Scanning.csproj" />
  </ItemGroup>
</Project>
```

Run:

```bash
dotnet sln Pennyworth.slnx add src/Pennyworth.Scanning/Pennyworth.Scanning.csproj tests/Pennyworth.Scanning.Tests/Pennyworth.Scanning.Tests.csproj
```

- [ ] **Step 2: Write the failing test**

`tests/Pennyworth.Scanning.Tests/SizeOnDiskTests.cs`:

```csharp
namespace Pennyworth.Scanning.Tests;

public class SizeOnDiskTests
{
    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 4096L)]
    [InlineData(4096L, 4096L)]
    [InlineData(4097L, 8192L)]
    public void Rounds_up_to_whole_clusters(long logical, long expected)
    {
        Assert.Equal(expected, SizeOnDisk.Estimate(FileAttributes.Normal, logical, 4096));
    }

    [Theory]
    [InlineData(FileAttributes.Offline)]
    [InlineData((FileAttributes)0x00400000)] // RecallOnDataAccess: OneDrive "online-only"
    public void Online_only_cloud_files_use_no_space(FileAttributes attributes)
    {
        Assert.Equal(0, SizeOnDisk.Estimate(attributes, 1_000_000, 4096));
    }

    [Fact]
    public void Sparse_and_compressed_files_use_their_stored_size()
    {
        Assert.True(SizeOnDisk.NeedsStoredSize(FileAttributes.SparseFile));
        Assert.True(SizeOnDisk.NeedsStoredSize(FileAttributes.Compressed));
        Assert.False(SizeOnDisk.NeedsStoredSize(FileAttributes.Normal));
        Assert.False(SizeOnDisk.NeedsStoredSize(FileAttributes.SparseFile | FileAttributes.Offline));
        Assert.Equal(8192, SizeOnDisk.Estimate(FileAttributes.SparseFile, 1_000_000_000_000, 4096, storedSize: 5000));
    }

    [Theory]
    [InlineData(FileAttributes.Directory, 0u, true)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, 0xA0000003u, false)] // junction / mount point
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, 0xA000000Cu, false)] // symbolic link
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, 0x9000001Au, true)]  // cloud files
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, 0x9000701Au, true)]  // cloud files, variant 7
    public void Enters_only_real_and_cloud_folders(FileAttributes attributes, uint tag, bool expected)
    {
        Assert.Equal(expected, SizeOnDisk.ShouldEnterDirectory(attributes, tag));
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --project tests/Pennyworth.Scanning.Tests`
Expected: build FAILS: `SizeOnDisk` does not exist.

- [ ] **Step 4: Implement the size rules**

`src/Pennyworth.Scanning/SizeOnDisk.cs`:

```csharp
namespace Pennyworth.Scanning;

/// <summary>Estimates allocated size from what a directory listing provides.</summary>
internal static class SizeOnDisk
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes OnlineOnly = FileAttributes.Offline | RecallOnDataAccess;

    private const uint CloudReparseTag = 0x9000001A;
    private const uint CloudReparseTagMask = 0x0000F000;

    /// <summary>
    /// Sparse and compressed files (for example WSL and Docker virtual disks) store far
    /// less than their logical size, so their real stored size must be asked for.
    /// </summary>
    public static bool NeedsStoredSize(FileAttributes attributes) =>
        (attributes & OnlineOnly) == 0
        && (attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0;

    /// <summary>
    /// Rounds up to whole clusters. Cloud files that are only online (OneDrive and
    /// similar) use no local space. Hard links are only exact in the full scan.
    /// </summary>
    /// <param name="storedSize">From <c>GetCompressedFileSize</c>, when <see cref="NeedsStoredSize"/> is true.</param>
    public static long Estimate(FileAttributes attributes, long logicalSize, long clusterSize, long? storedSize = null)
    {
        if ((attributes & OnlineOnly) != 0)
        {
            return 0;
        }

        long bytes = storedSize ?? logicalSize;
        return (bytes + clusterSize - 1) / clusterSize * clusterSize;
    }

    /// <summary>
    /// Directories that are reparse points (junctions, symbolic links, mount points) are
    /// skipped so nothing is counted twice, except cloud-sync folders, which hold real files.
    /// </summary>
    public static bool ShouldEnterDirectory(FileAttributes attributes, uint reparseTag) =>
        (attributes & FileAttributes.ReparsePoint) == 0
        || (reparseTag & ~CloudReparseTagMask) == CloudReparseTag;
}
```

- [ ] **Step 5: Implement the native interop**

`WIN32_FIND_DATAW` keeps each `FILETIME` as two `uint`s so the struct keeps Windows' 4-byte layout (592 bytes). `LibraryImport` generates AOT-safe marshalling. Paths get the `\\?\` prefix so long paths work.

`src/Pennyworth.Scanning/Native/NativeMethods.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Pennyworth.Scanning.Native;

internal static unsafe partial class NativeMethods
{
    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;
    public const int FindFirstExLargeFetch = 2;
    public const int ErrorFileNotFound = 2;
    public const int ErrorNoMoreFiles = 18;
    public static readonly nint InvalidHandleValue = -1;

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindFirstFileEx(
        string fileName,
        int infoLevel,
        Win32FindData* findData,
        int searchOp,
        nint searchFilter,
        int additionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextFile(nint findHandle, Win32FindData* findData);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(nint findHandle);

    public const uint InvalidFileSize = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(
        string rootPathName,
        out uint sectorsPerCluster,
        out uint bytesPerSector,
        out uint numberOfFreeClusters,
        out uint totalNumberOfClusters);
}

/// <summary>WIN32_FIND_DATAW. FILETIMEs are kept as uint pairs to preserve the 4-byte layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Win32FindData
{
    public uint FileAttributes;
    public uint CreationTimeLow;
    public uint CreationTimeHigh;
    public uint LastAccessTimeLow;
    public uint LastAccessTimeHigh;
    public uint LastWriteTimeLow;
    public uint LastWriteTimeHigh;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint Reserved0;
    public uint Reserved1;
    public fixed char FileName[260];
    public fixed char AlternateFileName[14];
}
```

`src/Pennyworth.Scanning/Native/NativeDirectory.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Pennyworth.Scanning.Native;

internal readonly record struct DirectoryEntry(string Name, FileAttributes Attributes, long Size, uint ReparseTag)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

internal static unsafe class NativeDirectory
{
    /// <summary>
    /// Lists one directory without following anything. Returns false when Windows refuses
    /// access (or the folder vanished), so callers can mark it as locked.
    /// </summary>
    public static bool TryList(string directoryPath, List<DirectoryEntry> entries)
    {
        string search = ToExtendedPath(Path.Join(directoryPath, "*"));
        Win32FindData data;
        nint handle = NativeMethods.FindFirstFileEx(
            search,
            NativeMethods.FindExInfoBasic,
            &data,
            NativeMethods.FindExSearchNameMatch,
            0,
            NativeMethods.FindFirstExLargeFetch);

        if (handle == NativeMethods.InvalidHandleValue)
        {
            return Marshal.GetLastPInvokeError() == NativeMethods.ErrorFileNotFound;
        }

        try
        {
            do
            {
                string name = new(data.FileName);
                if (name is "." or "..")
                {
                    continue;
                }

                var attributes = (FileAttributes)data.FileAttributes;
                long size = ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
                uint reparseTag = (attributes & FileAttributes.ReparsePoint) != 0 ? data.Reserved0 : 0;
                entries.Add(new DirectoryEntry(name, attributes, size, reparseTag));
            }
            while (NativeMethods.FindNextFile(handle, &data));

            return Marshal.GetLastPInvokeError() == NativeMethods.ErrorNoMoreFiles;
        }
        finally
        {
            NativeMethods.FindClose(handle);
        }
    }

    /// <summary>
    /// Bytes actually stored for a sparse or compressed file, or null when Windows can't tell.
    /// </summary>
    public static long? GetStoredSize(string filePath)
    {
        uint low = NativeMethods.GetCompressedFileSize(ToExtendedPath(filePath), out uint high);
        if (low == NativeMethods.InvalidFileSize && Marshal.GetLastPInvokeError() != 0)
        {
            return null;
        }

        return ((long)high << 32) | low;
    }

    public static long GetClusterSize(string rootPath)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(rootPath)) ?? rootPath;
        return NativeMethods.GetDiskFreeSpace(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _)
            ? (long)sectorsPerCluster * bytesPerSector
            : 4096;
    }

    private static string ToExtendedPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project tests/Pennyworth.Scanning.Tests`
Expected: PASS, 12 tests.

- [ ] **Step 7: Commit**

```bash
git add Pennyworth.slnx src/Pennyworth.Scanning tests/Pennyworth.Scanning.Tests
git commit -m "feat(scanning): add native directory listing and size-on-disk rules"
```

---

### Task 6: Parallel directory scanner

**Files:**
- Create: `src/Pennyworth.Scanning/DirectoryScanner.cs`
- Test: `tests/Pennyworth.Scanning.Tests/DirectoryScannerTests.cs`

**Interfaces:**
- Consumes: `IStorageScanner`, `IPathClassifier`, `StorageTreeBuilder`, `ScanProgress` (Task 2); `NativeDirectory`, `SizeOnDisk`, `DirectoryEntry` (Task 5).
- Produces: `public sealed class DirectoryScanner(int? workerCount = null, TimeSpan? progressInterval = null) : IStorageScanner` (default workers: processor count clamped to 2–16; default progress every 150 ms, plus one final report).

Design: workers pop folders from a shared stack (depth-first keeps memory low), list them without holding the lock, classify and size entries without the lock, then add them to the builder under one lock. An outstanding-work counter ends the scan when it reaches zero. A folder that can't be listed is marked locked and the scan continues.

- [ ] **Step 1: Write the failing test**

The tests create real folders in `%TEMP%`. Junctions (`mklink /J`) and sparse files (`fsutil sparse setflag`) don't need admin rights.

`tests/Pennyworth.Scanning.Tests/DirectoryScannerTests.cs`:

```csharp
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Pennyworth.Core.Storage;

namespace Pennyworth.Scanning.Tests;

public sealed class DirectoryScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bm-scan-").FullName;
    private readonly List<string> _deniedDirectories = [];
    private readonly List<string> _junctions = [];

    public void Dispose()
    {
        _deniedDirectories.ForEach(RestoreAccess);
        _junctions.ForEach(junction => Directory.Delete(junction));
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Counts_every_file_and_rolls_up_sizes()
    {
        WriteFile("a.txt", 1000);
        WriteFile(@"docs\b.txt", 5000);
        WriteFile(@"docs\deep\c.txt", 300);

        var tree = await ScanAsync();

        Assert.Equal(3, tree.FileCount);
        Assert.Equal(6300, tree.Root.LogicalSize);
        Assert.True(tree.Root.SizeOnDisk >= tree.Root.LogicalSize);
        var docs = tree.Root.GetChildren().Single(n => n.Name == "docs");
        Assert.Equal(5300, docs.LogicalSize);
        Assert.Equal(Path.Combine(_root, "docs"), docs.FullPath);
    }

    [Fact]
    public async Task Applies_the_classifier_to_folders_and_inherits_it_below()
    {
        WriteFile(@"games\x\big.bin", 2000);
        WriteFile(@"other\small.bin", 10);

        var tree = await ScanAsync(new NameClassifier("games", StorageCategory.Games));

        var area = Assert.Single(tree.GetCategoryAreas(StorageCategory.Games));
        Assert.Equal("games", area.Node.Name);
        Assert.Equal(2000, area.Node.LogicalSize);
        Assert.Equal(tree.GetCategoryBytes(StorageCategory.Games), area.Bytes);
        Assert.True(tree.GetCategoryBytes(StorageCategory.Other) > 0);
    }

    [Fact]
    public async Task Classifies_files_in_the_scan_root()
    {
        WriteFile("hiberfil.sys", 4096);
        WriteFile(@"sub\hiberfil.sys", 4096);

        var tree = await ScanAsync(new NameClassifier("hiberfil.sys", StorageCategory.HiddenSystem));

        Assert.Equal(4096, tree.GetCategoryBytes(StorageCategory.HiddenSystem));
    }

    [Fact]
    public async Task Counts_sparse_files_by_their_stored_size()
    {
        string path = Path.Combine(_root, "disk.vhdx");
        using (File.Create(path))
        {
        }

        RunCmd($"fsutil sparse setflag \"{path}\"");
        using (var stream = new FileStream(path, FileMode.Open))
        {
            stream.SetLength(10L * 1024 * 1024 * 1024); // 10 GB logical, nothing written
        }

        var tree = await ScanAsync();

        var disk = Assert.Single(tree.Root.GetChildren());
        Assert.Equal(10L * 1024 * 1024 * 1024, disk.LogicalSize);
        Assert.True(disk.SizeOnDisk < 1024 * 1024, $"Expected almost nothing stored, got {disk.SizeOnDisk}.");
    }

    [Fact]
    public async Task Does_not_follow_junctions()
    {
        WriteFile(@"real\data.bin", 8000);
        CreateJunction(Path.Combine(_root, "link"), Path.Combine(_root, "real"));

        var tree = await ScanAsync();

        Assert.Equal(1, tree.FileCount);
        Assert.DoesNotContain(tree.Root.GetChildren(), n => n.Name == "link");
    }

    [Fact]
    public async Task Marks_unreadable_folders_as_locked_and_keeps_going()
    {
        WriteFile(@"secret\hidden.bin", 100);
        WriteFile("visible.bin", 100);
        DenyListing(Path.Combine(_root, "secret"));

        var tree = await ScanAsync();

        var secret = tree.Root.GetChildren().Single(n => n.Name == "secret");
        Assert.True(secret.IsLocked);
        Assert.Equal(1, tree.FileCount);
    }

    [Fact]
    public async Task Reports_progress_with_category_totals()
    {
        WriteFile("a.bin", 100);
        var reports = new List<ScanProgress>();

        await new DirectoryScanner(workerCount: 2).ScanAsync(_root, new NameClassifier("none", StorageCategory.Other), new SyncProgress(reports), CancellationToken.None);

        Assert.NotEmpty(reports);
        ScanProgress last = reports[^1];
        Assert.Equal(1, last.FileCount);
        Assert.Equal(StorageCategories.Count, last.CategoryBytes.Count);
    }

    [Fact]
    public async Task Stops_when_cancelled()
    {
        WriteFile("a.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DirectoryScanner().ScanAsync(_root, new NameClassifier("x", StorageCategory.Other), null, cts.Token));
    }

    private Task<StorageTree> ScanAsync(IPathClassifier? classifier = null) =>
        new DirectoryScanner(workerCount: 4).ScanAsync(
            _root,
            classifier ?? new NameClassifier("none", StorageCategory.Other),
            null,
            TestContext.Current.CancellationToken);

    private void WriteFile(string relativePath, int length)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    private void CreateJunction(string link, string target)
    {
        _junctions.Add(link);
        RunCmd($"mklink /J \"{link}\" \"{target}\"");
    }

    private static void RunCmd(string command)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {command}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private void DenyListing(string directory)
    {
        _deniedDirectories.Add(directory);
        var info = new DirectoryInfo(directory);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    private static void RestoreAccess(string directory)
    {
        var info = new DirectoryInfo(directory);
        DirectorySecurity security = info.GetAccessControl();
        security.RemoveAccessRuleAll(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    /// <summary>Classifies any path whose last segment equals <paramref name="name"/>.</summary>
    private sealed class NameClassifier(string name, StorageCategory category) : IPathClassifier
    {
        public StorageCategory Classify(string fullPath, StorageCategory inherited) =>
            string.Equals(Path.GetFileName(fullPath), name, StringComparison.OrdinalIgnoreCase) ? category : inherited;
    }

    private sealed class SyncProgress(List<ScanProgress> reports) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Pennyworth.Scanning.Tests`
Expected: build FAILS: `DirectoryScanner` does not exist.

- [ ] **Step 3: Implement**

`src/Pennyworth.Scanning/DirectoryScanner.cs`:

```csharp
using System.Collections.Concurrent;
using Pennyworth.Core.Storage;
using Pennyworth.Scanning.Native;

namespace Pennyworth.Scanning;

/// <summary>
/// Scans any drive or folder without admin rights by listing directories in parallel.
/// Folders Windows refuses to list are recorded as locked and the scan continues.
/// </summary>
public sealed class DirectoryScanner : IStorageScanner
{
    private readonly int _workerCount;
    private readonly TimeSpan _progressInterval;

    public DirectoryScanner(int? workerCount = null, TimeSpan? progressInterval = null)
    {
        _workerCount = workerCount ?? Math.Clamp(Environment.ProcessorCount, 2, 16);
        _progressInterval = progressInterval ?? TimeSpan.FromMilliseconds(150);
    }

    public Task<StorageTree> ScanAsync(
        string rootPath,
        IPathClassifier classifier,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => new ScanRun(rootPath, classifier, _workerCount).Run(progress, _progressInterval, cancellationToken), cancellationToken);
    }

    private sealed class ScanRun
    {
        private readonly IPathClassifier _classifier;
        private readonly int _workerCount;
        private readonly long _clusterSize;
        private readonly StorageTreeBuilder _builder;
        private readonly object _builderLock = new();
        private readonly ConcurrentStack<WorkItem> _pending = new();
        private readonly SemaphoreSlim _available = new(0);
        private int _outstanding;

        public ScanRun(string rootPath, IPathClassifier classifier, int workerCount)
        {
            _classifier = classifier;
            _workerCount = workerCount;
            _clusterSize = NativeDirectory.GetClusterSize(rootPath);
            StorageCategory rootCategory = classifier.Classify(rootPath, StorageCategory.Other);
            _builder = new StorageTreeBuilder(rootPath, rootCategory);
            Enqueue(new WorkItem(StorageTreeBuilder.RootIndex, rootPath, rootCategory, IsRoot: true));
        }

        public StorageTree Run(IProgress<ScanProgress>? progress, TimeSpan progressInterval, CancellationToken cancellationToken)
        {
            using var timer = progress is null ? null : new Timer(_ => Report(progress), null, progressInterval, progressInterval);
            Task[] workers = Enumerable.Range(0, _workerCount)
                .Select(_ => Task.Factory.StartNew(() => Work(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                .ToArray();

            try
            {
                Task.WaitAll(workers, cancellationToken);
            }
            catch (AggregateException) when (cancellationToken.IsCancellationRequested)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            timer?.Dispose();
            if (progress is not null)
            {
                Report(progress);
            }

            lock (_builderLock)
            {
                return _builder.Build();
            }
        }

        private void Work(CancellationToken cancellationToken)
        {
            var entries = new List<DirectoryEntry>();
            while (true)
            {
                _available.Wait(cancellationToken);
                if (!_pending.TryPop(out WorkItem item))
                {
                    return; // Woken up because the scan is finished.
                }

                try
                {
                    entries.Clear();
                    Process(item, entries);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _outstanding) == 0)
                    {
                        _available.Release(_workerCount);
                    }
                }
            }
        }

        private void Process(WorkItem item, List<DirectoryEntry> entries)
        {
            if (!NativeDirectory.TryList(item.Path, entries))
            {
                lock (_builderLock)
                {
                    _builder.MarkLocked(item.Index);
                }

                return;
            }

            // Classify and size outside the lock; the lock only guards the builder.
            var directories = new List<(DirectoryEntry Entry, string Path, StorageCategory Category)>();
            var files = new List<(DirectoryEntry Entry, long Allocated, StorageCategory Category)>(entries.Count);
            foreach (DirectoryEntry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (SizeOnDisk.ShouldEnterDirectory(entry.Attributes, entry.ReparseTag))
                    {
                        string path = Path.Join(item.Path, entry.Name);
                        directories.Add((entry, path, _classifier.Classify(path, item.Category)));
                    }
                }
                else
                {
                    StorageCategory category = item.IsRoot
                        ? _classifier.Classify(Path.Join(item.Path, entry.Name), item.Category)
                        : item.Category;
                    long? stored = SizeOnDisk.NeedsStoredSize(entry.Attributes)
                        ? NativeDirectory.GetStoredSize(Path.Join(item.Path, entry.Name))
                        : null;
                    files.Add((entry, SizeOnDisk.Estimate(entry.Attributes, entry.Size, _clusterSize, stored), category));
                }
            }

            var children = new WorkItem[directories.Count];
            lock (_builderLock)
            {
                foreach (var (entry, allocated, category) in files)
                {
                    _builder.AddFile(item.Index, entry.Name, allocated, entry.Size, category);
                }

                for (int i = 0; i < directories.Count; i++)
                {
                    var (entry, path, category) = directories[i];
                    int index = _builder.AddDirectory(item.Index, entry.Name, category);
                    children[i] = new WorkItem(index, path, category, IsRoot: false);
                }
            }

            foreach (WorkItem child in children)
            {
                Enqueue(child);
            }
        }

        private void Enqueue(WorkItem item)
        {
            Interlocked.Increment(ref _outstanding);
            _pending.Push(item);
            _available.Release();
        }

        private void Report(IProgress<ScanProgress> progress)
        {
            ScanProgress snapshot;
            lock (_builderLock)
            {
                snapshot = new ScanProgress(_builder.FileCount, _builder.TotalBytes, _builder.SnapshotCategoryBytes());
            }

            progress.Report(snapshot);
        }
    }

    private readonly record struct WorkItem(int Index, string Path, StorageCategory Category, bool IsRoot);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/Pennyworth.Scanning.Tests`
Expected: PASS, 20 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Pennyworth.Scanning tests/Pennyworth.Scanning.Tests
git commit -m "feat(scanning): add parallel non-admin directory scanner"
```

---

### Task 7: App shell, strings, theme tokens and services

**Files:**
- Create: `src/Pennyworth.App/Pennyworth.App.csproj`, `app.manifest`, `Program.cs`
- Create: `src/Pennyworth.App/Resources/Strings.resx`, `Resources/Strings.pt-BR.resx`
- Create: `src/Pennyworth.App/Theme/Tokens.axaml`, `Theme/Styles.axaml`
- Create: `src/Pennyworth.App/Services/DriveService.cs`, `Services/ExplorerLauncher.cs`, `Services/LocationInfo.cs`
- Test: `tests/Pennyworth.App.Tests/Pennyworth.App.Tests.csproj`, `StringsTests.cs`

**Interfaces:**
- Consumes: `RuleClassifier.FindLocation` (Task 4), `LocalizedText.Get` (Task 3).
- Produces (namespace `Pennyworth.App.Services` unless noted):
  - `sealed record DriveSummary(string RootPath, string Label, long TotalBytes, long FreeBytes)` with `UsedBytes`.
  - `interface IDriveService { IReadOnlyList<DriveSummary> GetDrives(); string SystemDriveRoot { get; } }`; `WindowsDriveService` (fixed and removable drives that are ready).
  - `interface IExplorerLauncher { void ShowInExplorer(string path); }`; `ExplorerLauncher` (`explorer.exe /select,"path"`, or opens a drive root).
  - `interface ILocationInfo { (string Title, string Description)? Describe(string fullPath); }`; `KnowledgeBaseLocationInfo(RuleClassifier)` (uses `CultureInfo.CurrentUICulture`).
  - `Pennyworth.App.Resources.Strings`: strongly typed resources generated at build time by MSBuild (`StronglyTyped*` metadata). Keys: `AppTitle, StorageTitle, LocalDisk, DriveUsed {0}{1}, ScanningFiles {0:N0}, ScanComplete {0:N0}, Rescan, Category_Games, Category_Apps, Category_YourFiles, Category_Windows, Category_HiddenSystem, Category_Other, Category_Unaccounted, UnaccountedHint, OvercountHint, BiggestPlaces, BiggestPlacesPending, BrowseAllFolders, Back, Up, ShowInExplorer, Locked, LockedHint, EmptyFolder`.
  - Theme resource keys: `Brush.Background, Brush.Surface, Brush.SurfaceHover, Brush.Border, Brush.Track, Brush.TextPrimary, Brush.TextSecondary, Brush.Accent, Brush.Warning` (light + dark), `Brush.Category.{Games|Apps|YourFiles|Windows|HiddenSystem|Other|Unaccounted}`, `FontSize.{Title|Heading|Body|Caption}`, `Radius.{Card|Control}`, `Padding.{Page|Card|Row}`; style classes `TextBlock.title|heading|secondary|caption|warning`, `Border.card`, `Button.link`.

The real `Program.cs` starts Avalonia with the `App` class, which only arrives with the views in Task 11. A WinExe needs an entry point to build, so this task adds a **temporary `Program.cs`** that does nothing; Task 11 replaces it.

- [ ] **Step 1: Create the project, manifest and temporary entry point**

`src/Pennyworth.App/Pennyworth.App.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <AssemblyName>Pennyworth</AssemblyName>
    <RootNamespace>Pennyworth.App</RootNamespace>
    <IsAotCompatible>true</IsAotCompatible>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <SatelliteResourceLanguages>en;pt-BR</SatelliteResourceLanguages>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Pennyworth.App.Tests" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" />
    <PackageReference Include="Avalonia.Desktop" />
    <PackageReference Include="Avalonia.Themes.Simple" />
    <PackageReference Include="CommunityToolkit.Mvvm" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Pennyworth.Core\Pennyworth.Core.csproj" />
    <ProjectReference Include="..\Pennyworth.Insights\Pennyworth.Insights.csproj" />
    <ProjectReference Include="..\Pennyworth.Scanning\Pennyworth.Scanning.csproj" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Update="Resources\Strings.resx">
      <StronglyTypedFileName>$(IntermediateOutputPath)Strings.Designer.cs</StronglyTypedFileName>
      <StronglyTypedLanguage>CSharp</StronglyTypedLanguage>
      <StronglyTypedNamespace>Pennyworth.App.Resources</StronglyTypedNamespace>
      <StronglyTypedClassName>Strings</StronglyTypedClassName>
      <PublicClass>true</PublicClass>
    </EmbeddedResource>
  </ItemGroup>
</Project>
```

`src/Pennyworth.App/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="Pennyworth.App" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <!-- The UI never runs elevated; admin work goes to a separate helper (milestone 2). -->
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10 and 11 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

`src/Pennyworth.App/Program.cs` (temporary; replaced in Task 11):

```csharp
namespace Pennyworth.App;

internal static class Program
{
    [STAThread]
    public static int Main() => 0;
}
```

`tests/Pennyworth.App.Tests/Pennyworth.App.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
    <PackageReference Include="xunit.v3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Pennyworth.App\Pennyworth.App.csproj" />
  </ItemGroup>
</Project>
```

Run:

```bash
dotnet sln Pennyworth.slnx add src/Pennyworth.App/Pennyworth.App.csproj tests/Pennyworth.App.Tests/Pennyworth.App.Tests.csproj
```

- [ ] **Step 2: Write the failing test**

`tests/Pennyworth.App.Tests/StringsTests.cs`:

```csharp
using System.Collections;
using System.Globalization;
using System.Resources;
using Pennyworth.App.Resources;

namespace Pennyworth.App.Tests;

public class StringsTests
{
    [Fact]
    public void Every_string_has_a_portuguese_translation()
    {
        var manager = Strings.ResourceManager;
        var english = Keys(manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!);
        var portuguese = Keys(manager.GetResourceSet(CultureInfo.GetCultureInfo("pt-BR"), createIfNotExists: true, tryParents: false)!);

        Assert.Empty(english.Except(portuguese));
        Assert.Empty(portuguese.Except(english));
    }

    [Fact]
    public void Formatted_strings_accept_their_arguments()
    {
        foreach (string culture in new[] { "en-US", "pt-BR" })
        {
            var info = CultureInfo.GetCultureInfo(culture);
            _ = string.Format(info, Strings.ResourceManager.GetString(nameof(Strings.DriveUsed), info)!, "1 GB", "2 GB");
            _ = string.Format(info, Strings.ResourceManager.GetString(nameof(Strings.ScanningFiles), info)!, 1234L);
            _ = string.Format(info, Strings.ResourceManager.GetString(nameof(Strings.ScanComplete), info)!, 1234L);
        }
    }

    private static HashSet<string> Keys(ResourceSet set) =>
        set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet();
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --project tests/Pennyworth.App.Tests`
Expected: build FAILS: `Pennyworth.App.Resources` / `Strings` does not exist (no `.resx` yet).

- [ ] **Step 4: Add the strings**

`src/Pennyworth.App/Resources/Strings.resx`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <data name="AppTitle" xml:space="preserve"><value>Pennyworth</value></data>
  <data name="StorageTitle" xml:space="preserve"><value>Where is my space?</value></data>
  <data name="LocalDisk" xml:space="preserve"><value>Local Disk</value></data>
  <data name="DriveUsed" xml:space="preserve"><value>{0} used of {1}</value></data>
  <data name="ScanningFiles" xml:space="preserve"><value>Scanning… {0:N0} files found</value></data>
  <data name="ScanComplete" xml:space="preserve"><value>Scanned {0:N0} files</value></data>
  <data name="Rescan" xml:space="preserve"><value>Scan again</value></data>
  <data name="Category_Games" xml:space="preserve"><value>Games</value></data>
  <data name="Category_Apps" xml:space="preserve"><value>Apps</value></data>
  <data name="Category_YourFiles" xml:space="preserve"><value>Your files</value></data>
  <data name="Category_Windows" xml:space="preserve"><value>Windows</value></data>
  <data name="Category_HiddenSystem" xml:space="preserve"><value>Hidden system</value></data>
  <data name="Category_Other" xml:space="preserve"><value>Other</value></data>
  <data name="Category_Unaccounted" xml:space="preserve"><value>Not measured yet</value></data>
  <data name="UnaccountedHint" xml:space="preserve"><value>Space Windows reports as used that this scan can't see: protected system areas, restore points and NTFS internals. The full scan will measure it.</value></data>
  <data name="OvercountHint" xml:space="preserve"><value>Some files were counted more than once (hard links), so totals are a little high. The full scan will show exact numbers.</value></data>
  <data name="BiggestPlaces" xml:space="preserve"><value>Biggest places</value></data>
  <data name="BiggestPlacesPending" xml:space="preserve"><value>The biggest places appear here when the scan finishes.</value></data>
  <data name="BrowseAllFolders" xml:space="preserve"><value>Browse all folders</value></data>
  <data name="Back" xml:space="preserve"><value>Back</value></data>
  <data name="Up" xml:space="preserve"><value>Up</value></data>
  <data name="ShowInExplorer" xml:space="preserve"><value>Show in Explorer</value></data>
  <data name="Locked" xml:space="preserve"><value>Locked</value></data>
  <data name="LockedHint" xml:space="preserve"><value>Windows didn't allow this folder to be read.</value></data>
  <data name="EmptyFolder" xml:space="preserve"><value>This folder is empty.</value></data>
</root>
```

`src/Pennyworth.App/Resources/Strings.pt-BR.resx`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <data name="AppTitle" xml:space="preserve"><value>Pennyworth</value></data>
  <data name="StorageTitle" xml:space="preserve"><value>Para onde foi meu espaço?</value></data>
  <data name="LocalDisk" xml:space="preserve"><value>Disco Local</value></data>
  <data name="DriveUsed" xml:space="preserve"><value>{0} usados de {1}</value></data>
  <data name="ScanningFiles" xml:space="preserve"><value>Verificando… {0:N0} arquivos encontrados</value></data>
  <data name="ScanComplete" xml:space="preserve"><value>{0:N0} arquivos verificados</value></data>
  <data name="Rescan" xml:space="preserve"><value>Verificar de novo</value></data>
  <data name="Category_Games" xml:space="preserve"><value>Jogos</value></data>
  <data name="Category_Apps" xml:space="preserve"><value>Apps</value></data>
  <data name="Category_YourFiles" xml:space="preserve"><value>Seus arquivos</value></data>
  <data name="Category_Windows" xml:space="preserve"><value>Windows</value></data>
  <data name="Category_HiddenSystem" xml:space="preserve"><value>Sistema oculto</value></data>
  <data name="Category_Other" xml:space="preserve"><value>Outros</value></data>
  <data name="Category_Unaccounted" xml:space="preserve"><value>Ainda não medido</value></data>
  <data name="UnaccountedHint" xml:space="preserve"><value>Espaço que o Windows informa como usado, mas que esta verificação não consegue ver: áreas protegidas do sistema, pontos de restauração e estruturas internas do NTFS. A verificação completa vai medi-lo.</value></data>
  <data name="OvercountHint" xml:space="preserve"><value>Alguns arquivos foram contados mais de uma vez (links físicos), então os totais estão um pouco altos. A verificação completa mostrará os números exatos.</value></data>
  <data name="BiggestPlaces" xml:space="preserve"><value>Maiores locais</value></data>
  <data name="BiggestPlacesPending" xml:space="preserve"><value>Os maiores locais aparecem aqui quando a verificação terminar.</value></data>
  <data name="BrowseAllFolders" xml:space="preserve"><value>Ver todas as pastas</value></data>
  <data name="Back" xml:space="preserve"><value>Voltar</value></data>
  <data name="Up" xml:space="preserve"><value>Subir</value></data>
  <data name="ShowInExplorer" xml:space="preserve"><value>Mostrar no Explorador</value></data>
  <data name="Locked" xml:space="preserve"><value>Bloqueada</value></data>
  <data name="LockedHint" xml:space="preserve"><value>O Windows não permitiu ler esta pasta.</value></data>
  <data name="EmptyFolder" xml:space="preserve"><value>Esta pasta está vazia.</value></data>
</root>
```

- [ ] **Step 5: Add the theme tokens and styles**

`src/Pennyworth.App/Theme/Tokens.axaml`:

```xml
<!--
  Design tokens. The final visual design (from Claude Design) replaces the values here;
  views and controls only reference these keys, never raw colours or sizes.
-->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light">
      <SolidColorBrush x:Key="Brush.Background" Color="#F6F7F9" />
      <SolidColorBrush x:Key="Brush.Surface" Color="#FFFFFF" />
      <SolidColorBrush x:Key="Brush.SurfaceHover" Color="#EEF1F5" />
      <SolidColorBrush x:Key="Brush.Border" Color="#E2E6EC" />
      <SolidColorBrush x:Key="Brush.Track" Color="#E7EAF0" />
      <SolidColorBrush x:Key="Brush.TextPrimary" Color="#1B1F24" />
      <SolidColorBrush x:Key="Brush.TextSecondary" Color="#5E6772" />
      <SolidColorBrush x:Key="Brush.Accent" Color="#3B6FE0" />
      <SolidColorBrush x:Key="Brush.Warning" Color="#B26A00" />
    </ResourceDictionary>
    <ResourceDictionary x:Key="Dark">
      <SolidColorBrush x:Key="Brush.Background" Color="#15171B" />
      <SolidColorBrush x:Key="Brush.Surface" Color="#1E2126" />
      <SolidColorBrush x:Key="Brush.SurfaceHover" Color="#272B31" />
      <SolidColorBrush x:Key="Brush.Border" Color="#30353C" />
      <SolidColorBrush x:Key="Brush.Track" Color="#2B2F36" />
      <SolidColorBrush x:Key="Brush.TextPrimary" Color="#ECEEF1" />
      <SolidColorBrush x:Key="Brush.TextSecondary" Color="#9AA3AD" />
      <SolidColorBrush x:Key="Brush.Accent" Color="#6F9BFF" />
      <SolidColorBrush x:Key="Brush.Warning" Color="#F0A63A" />
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>

  <!-- Category colours (same in both themes for now). -->
  <SolidColorBrush x:Key="Brush.Category.Games" Color="#8B5CF6" />
  <SolidColorBrush x:Key="Brush.Category.Apps" Color="#3B82F6" />
  <SolidColorBrush x:Key="Brush.Category.YourFiles" Color="#10B981" />
  <SolidColorBrush x:Key="Brush.Category.Windows" Color="#F59E0B" />
  <SolidColorBrush x:Key="Brush.Category.HiddenSystem" Color="#EF4444" />
  <SolidColorBrush x:Key="Brush.Category.Other" Color="#94A3B8" />
  <SolidColorBrush x:Key="Brush.Category.Unaccounted" Color="#475569" />

  <x:Double x:Key="FontSize.Title">24</x:Double>
  <x:Double x:Key="FontSize.Heading">16</x:Double>
  <x:Double x:Key="FontSize.Body">14</x:Double>
  <x:Double x:Key="FontSize.Caption">12</x:Double>

  <CornerRadius x:Key="Radius.Card">10</CornerRadius>
  <CornerRadius x:Key="Radius.Control">6</CornerRadius>
  <Thickness x:Key="Padding.Page">24</Thickness>
  <Thickness x:Key="Padding.Card">16</Thickness>
  <Thickness x:Key="Padding.Row">12,8</Thickness>
</ResourceDictionary>
```

`src/Pennyworth.App/Theme/Styles.axaml`:

```xml
<!-- Control styles built only from Tokens.axaml keys. Replaced wholesale by the final design. -->
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style Selector="Window">
    <Setter Property="Background" Value="{DynamicResource Brush.Background}" />
    <Setter Property="Foreground" Value="{DynamicResource Brush.TextPrimary}" />
    <Setter Property="FontSize" Value="{StaticResource FontSize.Body}" />
  </Style>

  <Style Selector="TextBlock.title">
    <Setter Property="FontSize" Value="{StaticResource FontSize.Title}" />
    <Setter Property="FontWeight" Value="SemiBold" />
  </Style>
  <Style Selector="TextBlock.heading">
    <Setter Property="FontSize" Value="{StaticResource FontSize.Heading}" />
    <Setter Property="FontWeight" Value="SemiBold" />
  </Style>
  <Style Selector="TextBlock.secondary">
    <Setter Property="Foreground" Value="{DynamicResource Brush.TextSecondary}" />
  </Style>
  <Style Selector="TextBlock.caption">
    <Setter Property="FontSize" Value="{StaticResource FontSize.Caption}" />
    <Setter Property="Foreground" Value="{DynamicResource Brush.TextSecondary}" />
  </Style>
  <Style Selector="TextBlock.warning">
    <Setter Property="Foreground" Value="{DynamicResource Brush.Warning}" />
  </Style>

  <Style Selector="Border.card">
    <Setter Property="Background" Value="{DynamicResource Brush.Surface}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Brush.Border}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="CornerRadius" Value="{StaticResource Radius.Card}" />
    <Setter Property="Padding" Value="{StaticResource Padding.Card}" />
  </Style>

  <Style Selector="Button">
    <Setter Property="Background" Value="{DynamicResource Brush.Surface}" />
    <Setter Property="Foreground" Value="{DynamicResource Brush.TextPrimary}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Brush.Border}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="CornerRadius" Value="{StaticResource Radius.Control}" />
    <Setter Property="Padding" Value="{StaticResource Padding.Row}" />
    <Setter Property="Cursor" Value="Hand" />
  </Style>
  <Style Selector="Button:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource Brush.SurfaceHover}" />
  </Style>
  <Style Selector="Button.link">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="Foreground" Value="{DynamicResource Brush.Accent}" />
    <Setter Property="Padding" Value="4,2" />
  </Style>

  <Style Selector="ListBox">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderThickness" Value="0" />
  </Style>
  <Style Selector="ListBoxItem">
    <Setter Property="Padding" Value="{StaticResource Padding.Row}" />
    <Setter Property="CornerRadius" Value="{StaticResource Radius.Control}" />
  </Style>
  <Style Selector="ListBoxItem:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource Brush.SurfaceHover}" />
  </Style>
  <Style Selector="ListBoxItem:selected /template/ ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource Brush.SurfaceHover}" />
  </Style>

  <Style Selector="ProgressBar">
    <Setter Property="Background" Value="{DynamicResource Brush.Track}" />
    <Setter Property="Foreground" Value="{DynamicResource Brush.Accent}" />
    <Setter Property="MinHeight" Value="6" />
    <Setter Property="CornerRadius" Value="3" />
  </Style>
</Styles>
```

- [ ] **Step 6: Add the services**

`src/Pennyworth.App/Services/DriveService.cs`:

```csharp
namespace Pennyworth.App.Services;

public sealed record DriveSummary(string RootPath, string Label, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
}

public interface IDriveService
{
    IReadOnlyList<DriveSummary> GetDrives();

    string SystemDriveRoot { get; }
}

public sealed class WindowsDriveService : IDriveService
{
    public string SystemDriveRoot => Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    public IReadOnlyList<DriveSummary> GetDrives()
    {
        var drives = new List<DriveSummary>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            try
            {
                if (drive.IsReady)
                {
                    drives.Add(new DriveSummary(drive.RootDirectory.FullName, drive.VolumeLabel, drive.TotalSize, drive.TotalFreeSpace));
                }
            }
            catch (IOException)
            {
                // Drive removed or not readable right now; skip it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return drives;
    }
}
```

`src/Pennyworth.App/Services/ExplorerLauncher.cs`:

```csharp
using System.Diagnostics;

namespace Pennyworth.App.Services;

public interface IExplorerLauncher
{
    void ShowInExplorer(string path);
}

public sealed class ExplorerLauncher : IExplorerLauncher
{
    public void ShowInExplorer(string path)
    {
        bool isDriveRoot = Path.GetPathRoot(path) == path;
        string arguments = isDriveRoot ? $"\"{path}\"" : $"/select,\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }
}
```

`src/Pennyworth.App/Services/LocationInfo.cs`:

```csharp
using System.Globalization;
using Pennyworth.Insights.Classification;

namespace Pennyworth.App.Services;

/// <summary>Plain-language titles and descriptions for known folders.</summary>
public interface ILocationInfo
{
    (string Title, string Description)? Describe(string fullPath);
}

public sealed class KnowledgeBaseLocationInfo(RuleClassifier classifier) : ILocationInfo
{
    public (string Title, string Description)? Describe(string fullPath)
    {
        var location = classifier.FindLocation(fullPath);
        if (location is null)
        {
            return null;
        }

        CultureInfo culture = CultureInfo.CurrentUICulture;
        return (location.Title.Get(culture), location.Description.Get(culture));
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --project tests/Pennyworth.App.Tests`
Expected: PASS, 2 tests.

- [ ] **Step 8: Commit**

```bash
git add Pennyworth.slnx src/Pennyworth.App tests/Pennyworth.App.Tests
git commit -m "feat(app): add app project, EN/PT-BR strings, design tokens and services"
```

---

### Task 8: Storage summary view model

**Files:**
- Create: `src/Pennyworth.App/ViewModels/CategoryText.cs`, `ViewModels/StorageSummaryViewModel.cs`
- Test: `tests/Pennyworth.App.Tests/Fakes.cs`, `StorageSummaryViewModelTests.cs`

**Interfaces:**
- Consumes: `StorageTree`, `StorageNode`, `CategoryArea`, `ScanProgress`, `StorageCategories` (Task 2); `ByteSize` (Task 1); `DriveSummary`, `ILocationInfo`, `Strings` (Task 7).
- Produces (namespace `Pennyworth.App.ViewModels`):
  - `sealed record BarSegment(string ColorKey, double Fraction)` (colour = resource `Brush.Category.{ColorKey}`).
  - `StorageSummaryViewModel(DriveSummary drive, ILocationInfo locations, Action<StorageNode> openFolder)`: `Drive`, `Categories` (one `CategoryRowViewModel` per `StorageCategories.All`), `Unaccounted` (row with `Category == null`, `ColorKey == "Unaccounted"`), `UsedText`, `Segments`, `FileCount`, `HasOvercount`, `IsComplete`, `SelectedCategory`, `ApplyProgress(ScanProgress)` (ignored once complete), `ApplyResult(StorageTree)` (fills up to 10 areas per category, selects the largest category), `OpenAreaCommand(CategoryAreaViewModel)`, `BrowseAllCommand` (enabled when complete).
  - `CategoryRowViewModel(StorageCategory? category)`: `Category`, `Name`, `ColorKey`, `Swatch`, `Bytes`, `Fraction`, `SizeText`, `PercentText`, `Areas`.
  - `sealed record CategoryAreaViewModel(StorageNode Node, long Bytes, string Title, string? Description)` with `Path`, `SizeText`.
- Test fakes produced in `Fakes.cs` (used by Tasks 9 and 10): `SampleTrees.Build()` and `SampleTrees.Gb`, `FakeDriveService`, `FakeScanner` (with `ScannedRoots` and an optional `Gate`), `NullClassifier`, `FakeLocations`, `FakeExplorer` (with `Shown`).

Rule enforced here: the bar always adds up to 100%. The whole is the larger of "used space" and "scanned bytes": normally used space (so "Not measured yet" fills the gap); if hard links make the scan exceed used space, the scanned total, and `HasOvercount` shows a note.

- [ ] **Step 1: Write the fakes and the failing test**

`tests/Pennyworth.App.Tests/Fakes.cs`:

```csharp
using Pennyworth.App.Services;
using Pennyworth.Core.Storage;

namespace Pennyworth.App.Tests;

internal static class SampleTrees
{
    public const long Gb = 1024L * 1024 * 1024;

    // C:\  Windows\ (20 GB)  Games\ (50 GB)  Users\Bob\ (10 GB)  stray.bin (5 GB, Other)
    public static StorageTree Build()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        int windows = b.AddDirectory(StorageTreeBuilder.RootIndex, "Windows", StorageCategory.Windows);
        b.AddFile(windows, "system.dll", 20 * Gb, 20 * Gb, StorageCategory.Windows);
        int games = b.AddDirectory(StorageTreeBuilder.RootIndex, "Games", StorageCategory.Games);
        b.AddFile(games, "game.pak", 50 * Gb, 50 * Gb, StorageCategory.Games);
        int users = b.AddDirectory(StorageTreeBuilder.RootIndex, "Users", StorageCategory.Other);
        int bob = b.AddDirectory(users, "Bob", StorageCategory.YourFiles);
        b.AddFile(bob, "video.mp4", 10 * Gb, 10 * Gb, StorageCategory.YourFiles);
        b.AddFile(StorageTreeBuilder.RootIndex, "stray.bin", 5 * Gb, 5 * Gb, StorageCategory.Other);
        return b.Build();
    }
}

internal sealed class FakeDriveService(params DriveSummary[] drives) : IDriveService
{
    public string SystemDriveRoot { get; init; } = @"C:\";

    public IReadOnlyList<DriveSummary> GetDrives() => drives;
}

internal sealed class FakeScanner(Func<string, StorageTree> result) : IStorageScanner
{
    public List<string> ScannedRoots { get; } = [];

    public TaskCompletionSource? Gate { get; set; }

    public async Task<StorageTree> ScanAsync(string rootPath, IPathClassifier classifier, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ScannedRoots.Add(rootPath);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return result(rootPath);
    }
}

internal sealed class NullClassifier : IPathClassifier
{
    public StorageCategory Classify(string fullPath, StorageCategory inherited) => inherited;
}

internal sealed class FakeLocations(Dictionary<string, (string Title, string Description)>? known = null) : ILocationInfo
{
    public (string Title, string Description)? Describe(string fullPath) =>
        known is not null && known.TryGetValue(fullPath, out var value) ? value : null;
}

internal sealed class FakeExplorer : IExplorerLauncher
{
    public List<string> Shown { get; } = [];

    public void ShowInExplorer(string path) => Shown.Add(path);
}
```

`tests/Pennyworth.App.Tests/StorageSummaryViewModelTests.cs`:

```csharp
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Core.Storage;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

public class StorageSummaryViewModelTests
{
    private static readonly DriveSummary Drive = new(@"C:\", "", TotalBytes: 200 * Gb, FreeBytes: 100 * Gb);

    private static StorageSummaryViewModel Create(Action<StorageNode>? open = null, ILocationInfo? locations = null) =>
        new(Drive, locations ?? new FakeLocations(), open ?? (_ => { }));

    [Fact]
    public void Splits_used_space_into_categories_and_not_measured()
    {
        var summary = Create();

        summary.ApplyResult(Build());

        Assert.Equal(50 * Gb, summary.Categories.Single(c => c.Category == StorageCategory.Games).Bytes);
        Assert.Equal(0.5, summary.Categories.Single(c => c.Category == StorageCategory.Games).Fraction, 3);
        Assert.Equal(15 * Gb, summary.Unaccounted.Bytes); // 100 GB used - 85 GB scanned
        Assert.False(summary.HasOvercount);
        Assert.Equal(1.0, summary.Segments.Sum(s => s.Fraction), 3);
        Assert.Contains(summary.Segments, s => s.ColorKey == "Unaccounted");
    }

    [Fact]
    public void Flags_overcount_when_scan_exceeds_used_space()
    {
        var summary = new StorageSummaryViewModel(new DriveSummary(@"C:\", "", 200 * Gb, 150 * Gb), new FakeLocations(), _ => { });

        summary.ApplyResult(Build()); // 85 GB scanned, only 50 GB used

        Assert.True(summary.HasOvercount);
        Assert.Equal(0, summary.Unaccounted.Bytes);
        Assert.Equal(1.0, summary.Segments.Sum(s => s.Fraction), 3);
    }

    [Fact]
    public void Updates_live_from_progress_until_complete()
    {
        var summary = Create();
        long[] bytes = new long[StorageCategories.Count];
        bytes[(int)StorageCategory.Apps] = 7 * Gb;

        summary.ApplyProgress(new ScanProgress(42, 7 * Gb, bytes));

        Assert.Equal(7 * Gb, summary.Categories.Single(c => c.Category == StorageCategory.Apps).Bytes);
        Assert.Equal(42, summary.FileCount);
        Assert.False(summary.IsComplete);

        summary.ApplyResult(Build());
        summary.ApplyProgress(new ScanProgress(1, 1, new long[StorageCategories.Count]));

        Assert.Equal(4, summary.FileCount);
        Assert.Equal(0, summary.Categories.Single(c => c.Category == StorageCategory.Apps).Bytes);
    }

    [Fact]
    public void Selects_the_biggest_category_and_lists_its_areas_with_friendly_titles()
    {
        var locations = new FakeLocations(new() { [@"C:\Games"] = ("My games", "Where games live") });
        var summary = Create(locations: locations);

        summary.ApplyResult(Build());

        Assert.Equal(StorageCategory.Games, summary.SelectedCategory?.Category);
        var area = Assert.Single(summary.SelectedCategory!.Areas);
        Assert.Equal("My games", area.Title);
        Assert.Equal("Where games live", area.Description);
        Assert.Equal(@"C:\Games", area.Path);
    }

    [Fact]
    public void Falls_back_to_folder_name_for_unknown_areas()
    {
        var summary = Create();

        summary.ApplyResult(Build());

        var yourFiles = summary.Categories.Single(c => c.Category == StorageCategory.YourFiles);
        Assert.Equal("Bob", Assert.Single(yourFiles.Areas).Title);
    }

    [Fact]
    public void Browse_all_is_only_available_after_the_scan()
    {
        StorageNode? opened = null;
        var summary = Create(open: node => opened = node);

        Assert.False(summary.BrowseAllCommand.CanExecute(null));
        summary.ApplyResult(Build());
        summary.BrowseAllCommand.Execute(null);

        Assert.Equal(@"C:\", opened?.FullPath);
    }

    [Fact]
    public void Opening_an_area_opens_its_folder()
    {
        StorageNode? opened = null;
        var summary = Create(open: node => opened = node);
        summary.ApplyResult(Build());

        summary.OpenAreaCommand.Execute(summary.SelectedCategory!.Areas[0]);

        Assert.Equal(@"C:\Games", opened?.FullPath);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Pennyworth.App.Tests`
Expected: build FAILS: namespace `Pennyworth.App.ViewModels` does not exist.

- [ ] **Step 3: Implement**

`src/Pennyworth.App/ViewModels/CategoryText.cs`:

```csharp
using Pennyworth.App.Resources;
using Pennyworth.Core.Storage;

namespace Pennyworth.App.ViewModels;

internal static class CategoryText
{
    /// <summary>Resource key suffix for the bar colour, e.g. <c>Brush.Category.Games</c>.</summary>
    public const string UnaccountedKey = "Unaccounted";

    public static string Name(StorageCategory category) => category switch
    {
        StorageCategory.Games => Strings.Category_Games,
        StorageCategory.Apps => Strings.Category_Apps,
        StorageCategory.YourFiles => Strings.Category_YourFiles,
        StorageCategory.Windows => Strings.Category_Windows,
        StorageCategory.HiddenSystem => Strings.Category_HiddenSystem,
        _ => Strings.Category_Other,
    };

    public static string ColorKey(StorageCategory category) => category.ToString();
}
```

`src/Pennyworth.App/ViewModels/StorageSummaryViewModel.cs`:

```csharp
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <param name="ColorKey">Looked up as the resource <c>Brush.Category.{ColorKey}</c>.</param>
public sealed record BarSegment(string ColorKey, double Fraction);

/// <summary>The plain-language summary of one drive. Updates live while the scan runs.</summary>
public sealed partial class StorageSummaryViewModel : ObservableObject
{
    private const int MaxAreasPerCategory = 10;

    private readonly ILocationInfo _locations;
    private readonly Action<StorageNode> _openFolder;
    private StorageTree? _tree;

    public StorageSummaryViewModel(DriveSummary drive, ILocationInfo locations, Action<StorageNode> openFolder)
    {
        Drive = drive;
        _locations = locations;
        _openFolder = openFolder;
        Categories = StorageCategories.All.Select(category => new CategoryRowViewModel(category)).ToArray();
        Unaccounted = new CategoryRowViewModel(null);
        Update(new long[StorageCategories.Count], 0);
    }

    public DriveSummary Drive { get; }

    public IReadOnlyList<CategoryRowViewModel> Categories { get; }

    public CategoryRowViewModel Unaccounted { get; }

    public string UsedText => string.Format(Strings.DriveUsed, ByteSize.Format(Drive.UsedBytes), ByteSize.Format(Drive.TotalBytes));

    [ObservableProperty]
    public partial IReadOnlyList<BarSegment> Segments { get; private set; } = [];

    [ObservableProperty]
    public partial long FileCount { get; private set; }

    /// <summary>The scan counted more than Windows reports as used (hard links).</summary>
    [ObservableProperty]
    public partial bool HasOvercount { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseAllCommand))]
    public partial bool IsComplete { get; private set; }

    [ObservableProperty]
    public partial CategoryRowViewModel? SelectedCategory { get; set; }

    public void ApplyProgress(ScanProgress progress)
    {
        if (!IsComplete)
        {
            Update(progress.CategoryBytes, progress.FileCount);
        }
    }

    public void ApplyResult(StorageTree tree)
    {
        _tree = tree;
        long[] bytes = Enumerable.Range(0, StorageCategories.Count)
            .Select(i => tree.GetCategoryBytes((StorageCategory)i))
            .ToArray();
        Update(bytes, tree.FileCount);
        foreach (CategoryRowViewModel row in Categories)
        {
            row.Areas = tree.GetCategoryAreas(row.Category!.Value)
                .Take(MaxAreasPerCategory)
                .Select(CreateArea)
                .ToArray();
        }

        IsComplete = true;
        SelectedCategory ??= Categories.Where(row => row.Bytes > 0).MaxBy(row => row.Bytes);
    }

    [RelayCommand]
    private void OpenArea(CategoryAreaViewModel area) => _openFolder(area.Node);

    [RelayCommand(CanExecute = nameof(IsComplete))]
    private void BrowseAll() => _openFolder(_tree!.Root);

    private CategoryAreaViewModel CreateArea(CategoryArea area)
    {
        var description = _locations.Describe(area.Node.FullPath);
        return new CategoryAreaViewModel(area.Node, area.Bytes, description?.Title ?? area.Node.Name, description?.Description);
    }

    private void Update(IReadOnlyList<long> categoryBytes, long fileCount)
    {
        long scanned = categoryBytes.Sum();
        long unaccounted = Drive.UsedBytes - scanned;
        HasOvercount = unaccounted < 0;
        long whole = Math.Max(Math.Max(Drive.UsedBytes, scanned), 1);

        foreach (CategoryRowViewModel row in Categories)
        {
            row.SetBytes(categoryBytes[(int)row.Category!.Value], whole);
        }

        Unaccounted.SetBytes(Math.Max(unaccounted, 0), whole);
        FileCount = fileCount;
        Segments = Categories
            .Append(Unaccounted)
            .Where(row => row.Bytes > 0)
            .Select(row => new BarSegment(row.ColorKey, row.Fraction))
            .ToArray();
    }
}

public sealed partial class CategoryRowViewModel : ObservableObject
{
    /// <param name="category">Null for the "not measured yet" row.</param>
    public CategoryRowViewModel(StorageCategory? category)
    {
        Category = category;
        Name = category is { } c ? CategoryText.Name(c) : Strings.Category_Unaccounted;
        ColorKey = category is { } k ? CategoryText.ColorKey(k) : CategoryText.UnaccountedKey;
        Swatch = [new BarSegment(ColorKey, 1)];
    }

    public StorageCategory? Category { get; }

    public string Name { get; }

    public string ColorKey { get; }

    /// <summary>A single full segment, used to draw the category's colour dot.</summary>
    public IReadOnlyList<BarSegment> Swatch { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long Bytes { get; private set; }

    /// <summary>Share of the drive's used space, 0 to 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial double Fraction { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<CategoryAreaViewModel> Areas { get; set; } = [];

    public string SizeText => ByteSize.Format(Bytes);

    public string PercentText => Fraction.ToString("P0");

    internal void SetBytes(long bytes, long whole)
    {
        Bytes = bytes;
        Fraction = (double)bytes / whole;
    }
}

public sealed record CategoryAreaViewModel(StorageNode Node, long Bytes, string Title, string? Description)
{
    public string Path => Node.FullPath;

    public string SizeText => ByteSize.Format(Bytes);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/Pennyworth.App.Tests`
Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Pennyworth.App tests/Pennyworth.App.Tests
git commit -m "feat(app): add storage summary view model"
```

---

### Task 9: Folder browser view model

**Files:**
- Create: `src/Pennyworth.App/ViewModels/FolderBrowserViewModel.cs`
- Test: `tests/Pennyworth.App.Tests/FolderBrowserViewModelTests.cs`

**Interfaces:**
- Consumes: `StorageNode` (Task 2), `ByteSize` (Task 1), `IExplorerLauncher` (Task 7), fakes (Task 8).
- Produces: `FolderBrowserViewModel(StorageNode start, IExplorerLauncher explorer, Action back)`: `Current`, `Items : IReadOnlyList<FolderItemViewModel>` (largest first), `Breadcrumbs : IReadOnlyList<BreadcrumbViewModel>` (root first), `SizeText`, `IsEmpty`, `OpenCommand(FolderItemViewModel)` (folders only), `NavigateToCommand(BreadcrumbViewModel)`, `UpCommand` (disabled at the root), `ShowInExplorerCommand(FolderItemViewModel)`, `BackCommand`. Records `FolderItemViewModel(StorageNode Node, long ParentSize)` (`Name, IsDirectory, IsLocked, SizeText, Fraction`) and `BreadcrumbViewModel(StorageNode Node)` (`Name`).

- [ ] **Step 1: Write the failing test**

`tests/Pennyworth.App.Tests/FolderBrowserViewModelTests.cs`:

```csharp
using Pennyworth.App.ViewModels;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

public class FolderBrowserViewModelTests
{
    [Fact]
    public void Lists_children_largest_first_with_share_of_parent()
    {
        var browser = new FolderBrowserViewModel(Build().Root, new FakeExplorer(), () => { });

        Assert.Equal(["Games", "Windows", "Users", "stray.bin"], browser.Items.Select(i => i.Name));
        Assert.Equal(50.0 / 85, browser.Items[0].Fraction, 3);
    }

    [Fact]
    public void Opens_folders_and_ignores_files()
    {
        var browser = new FolderBrowserViewModel(Build().Root, new FakeExplorer(), () => { });

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "stray.bin"));
        Assert.Equal(@"C:\", browser.Current.FullPath);

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Users"));
        Assert.Equal(@"C:\Users", browser.Current.FullPath);
        Assert.Equal([@"C:\", "Users"], browser.Breadcrumbs.Select(b => b.Name));
    }

    [Fact]
    public void Goes_up_and_jumps_through_breadcrumbs()
    {
        var root = Build().Root;
        var bob = root.GetChildren().Single(n => n.Name == "Users").GetChildren().Single();
        var browser = new FolderBrowserViewModel(bob, new FakeExplorer(), () => { });

        browser.UpCommand.Execute(null);
        Assert.Equal(@"C:\Users", browser.Current.FullPath);

        browser.NavigateToCommand.Execute(browser.Breadcrumbs[0]);
        Assert.Equal(@"C:\", browser.Current.FullPath);
        Assert.False(browser.UpCommand.CanExecute(null));
    }

    [Fact]
    public void Shows_items_in_explorer_and_goes_back()
    {
        var explorer = new FakeExplorer();
        bool wentBack = false;
        var browser = new FolderBrowserViewModel(Build().Root, explorer, () => wentBack = true);

        browser.ShowInExplorerCommand.Execute(browser.Items[0]);
        browser.BackCommand.Execute(null);

        Assert.Equal([@"C:\Games"], explorer.Shown);
        Assert.True(wentBack);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Pennyworth.App.Tests`
Expected: build FAILS: `FolderBrowserViewModel` does not exist.

- [ ] **Step 3: Implement**

`src/Pennyworth.App/ViewModels/FolderBrowserViewModel.cs`:

```csharp
using Pennyworth.App.Services;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>Drill-down through folders, largest first.</summary>
public sealed partial class FolderBrowserViewModel : ObservableObject
{
    private readonly IExplorerLauncher _explorer;
    private readonly Action _back;

    public FolderBrowserViewModel(StorageNode start, IExplorerLauncher explorer, Action back)
    {
        _explorer = explorer;
        _back = back;
        Current = start;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Items), nameof(Breadcrumbs), nameof(SizeText), nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    public partial StorageNode Current { get; private set; }

    public IReadOnlyList<FolderItemViewModel> Items =>
        Current.GetChildren().Select(child => new FolderItemViewModel(child, Current.SizeOnDisk)).ToArray();

    public IReadOnlyList<BreadcrumbViewModel> Breadcrumbs
    {
        get
        {
            var crumbs = new List<BreadcrumbViewModel>();
            for (StorageNode? node = Current; node is { } current; node = current.Parent)
            {
                crumbs.Insert(0, new BreadcrumbViewModel(current));
            }

            return crumbs;
        }
    }

    public string SizeText => ByteSize.Format(Current.SizeOnDisk);

    public bool IsEmpty => !Current.HasChildren;

    [RelayCommand]
    private void Open(FolderItemViewModel item)
    {
        if (item.Node.IsDirectory)
        {
            Current = item.Node;
        }
    }

    [RelayCommand]
    private void NavigateTo(BreadcrumbViewModel crumb) => Current = crumb.Node;

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up() => Current = Current.Parent!.Value;

    [RelayCommand]
    private void ShowInExplorer(FolderItemViewModel item) => _explorer.ShowInExplorer(item.Node.FullPath);

    [RelayCommand]
    private void Back() => _back();

    private bool CanGoUp() => Current.Parent is not null;
}

public sealed record FolderItemViewModel(StorageNode Node, long ParentSize)
{
    public string Name => Node.Name;

    public bool IsDirectory => Node.IsDirectory;

    public bool IsLocked => Node.IsLocked;

    public string SizeText => ByteSize.Format(Node.SizeOnDisk);

    /// <summary>Share of the parent folder, 0 to 1, for the size bar.</summary>
    public double Fraction => ParentSize == 0 ? 0 : (double)Node.SizeOnDisk / ParentSize;
}

public sealed record BreadcrumbViewModel(StorageNode Node)
{
    public string Name => Node.Name;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/Pennyworth.App.Tests`
Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Pennyworth.App tests/Pennyworth.App.Tests
git commit -m "feat(app): add folder browser view model"
```

---

### Task 10: Main window view model

**Files:**
- Create: `src/Pennyworth.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Pennyworth.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `IStorageScanner`, `IPathClassifier`, `ScanProgress`, `StorageTree` (Task 2); `IDriveService`, `ILocationInfo`, `IExplorerLauncher`, `Strings` (Task 7); `StorageSummaryViewModel` (Task 8); `FolderBrowserViewModel` (Task 9).
- Produces:
  - `MainWindowViewModel(IDriveService drives, IStorageScanner scanner, IPathClassifier classifier, ILocationInfo locations, IExplorerLauncher explorer)`: `Drives`, `SelectedDrive` (setting it shows the cached summary or starts a scan, cancelling any running one), `CurrentPage` (`StorageSummaryViewModel` or `FolderBrowserViewModel`), `IsScanning`, `StatusText`, `CurrentScan : Task` (for tests), `Initialize()` (loads drives, selects the Windows drive), `RescanCommand` (disabled while scanning).
  - `DriveViewModel(DriveSummary summary)`: `Summary`, `Name` (`"Label (C:)"` or `"Local Disk (C:)"`), `UsedText`, `UsedFraction`.

- [ ] **Step 1: Write the failing test**

`tests/Pennyworth.App.Tests/MainWindowViewModelTests.cs`:

```csharp
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

public class MainWindowViewModelTests
{
    private static readonly DriveSummary DriveC = new(@"C:\", "", 200 * Gb, 100 * Gb);
    private static readonly DriveSummary DriveD = new(@"D:\", "Games", 500 * Gb, 400 * Gb);

    private static (MainWindowViewModel ViewModel, FakeScanner Scanner) Create()
    {
        var scanner = new FakeScanner(_ => Build());
        var viewModel = new MainWindowViewModel(
            new FakeDriveService(DriveD, DriveC),
            scanner,
            new NullClassifier(),
            new FakeLocations(),
            new FakeExplorer());
        return (viewModel, scanner);
    }

    [Fact]
    public async Task Starts_by_scanning_the_windows_drive()
    {
        var (viewModel, scanner) = Create();

        viewModel.Initialize();
        await viewModel.CurrentScan;

        Assert.Equal(2, viewModel.Drives.Count);
        Assert.Equal(@"C:\", viewModel.SelectedDrive?.Summary.RootPath);
        Assert.Equal([@"C:\"], scanner.ScannedRoots);
        var summary = Assert.IsType<StorageSummaryViewModel>(viewModel.CurrentPage);
        Assert.True(summary.IsComplete);
        Assert.False(viewModel.IsScanning);
    }

    [Fact]
    public async Task Reuses_finished_scans_when_switching_back()
    {
        var (viewModel, scanner) = Create();
        viewModel.Initialize();
        await viewModel.CurrentScan;

        viewModel.SelectedDrive = viewModel.Drives.Single(d => d.Summary.RootPath == @"D:\");
        await viewModel.CurrentScan;
        viewModel.SelectedDrive = viewModel.Drives.Single(d => d.Summary.RootPath == @"C:\");
        await viewModel.CurrentScan;

        Assert.Equal([@"C:\", @"D:\"], scanner.ScannedRoots);
    }

    [Fact]
    public async Task Rescan_scans_again()
    {
        var (viewModel, scanner) = Create();
        viewModel.Initialize();
        await viewModel.CurrentScan;

        viewModel.RescanCommand.Execute(null);
        await viewModel.CurrentScan;

        Assert.Equal([@"C:\", @"C:\"], scanner.ScannedRoots);
    }

    [Fact]
    public async Task Switching_drives_cancels_the_running_scan()
    {
        var (viewModel, scanner) = Create();
        scanner.Gate = new TaskCompletionSource();
        viewModel.Initialize();
        Task firstScan = viewModel.CurrentScan;
        Assert.True(viewModel.IsScanning);

        scanner.Gate = null;
        viewModel.SelectedDrive = viewModel.Drives.Single(d => d.Summary.RootPath == @"D:\");
        await viewModel.CurrentScan;
        await firstScan;

        var summary = Assert.IsType<StorageSummaryViewModel>(viewModel.CurrentPage);
        Assert.Equal(@"D:\", summary.Drive.RootPath);
        Assert.False(viewModel.IsScanning);
    }

    [Fact]
    public async Task Opening_a_folder_and_going_back_returns_to_the_summary()
    {
        var (viewModel, _) = Create();
        viewModel.Initialize();
        await viewModel.CurrentScan;
        var summary = Assert.IsType<StorageSummaryViewModel>(viewModel.CurrentPage);

        summary.BrowseAllCommand.Execute(null);
        var browser = Assert.IsType<FolderBrowserViewModel>(viewModel.CurrentPage);
        browser.BackCommand.Execute(null);

        Assert.Same(summary, viewModel.CurrentPage);
    }

    [Fact]
    public void Drive_names_use_label_or_local_disk()
    {
        Assert.Equal("Games (D:)", new DriveViewModel(DriveD).Name);
        Assert.EndsWith("(C:)", new DriveViewModel(DriveC).Name);
        Assert.Equal(0.5, new DriveViewModel(DriveC).UsedFraction, 3);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Pennyworth.App.Tests`
Expected: build FAILS: `MainWindowViewModel` / `DriveViewModel` do not exist.

- [ ] **Step 3: Implement**

`src/Pennyworth.App/ViewModels/MainWindowViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDriveService _drives;
    private readonly IStorageScanner _scanner;
    private readonly IPathClassifier _classifier;
    private readonly ILocationInfo _locations;
    private readonly IExplorerLauncher _explorer;
    private readonly Dictionary<string, StorageSummaryViewModel> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanCancellation;

    public MainWindowViewModel(
        IDriveService drives,
        IStorageScanner scanner,
        IPathClassifier classifier,
        ILocationInfo locations,
        IExplorerLauncher explorer)
    {
        _drives = drives;
        _scanner = scanner;
        _classifier = classifier;
        _locations = locations;
        _explorer = explorer;
    }

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    [ObservableProperty]
    public partial DriveViewModel? SelectedDrive { get; set; }

    /// <summary>A <see cref="StorageSummaryViewModel"/> or a <see cref="FolderBrowserViewModel"/>.</summary>
    [ObservableProperty]
    public partial object? CurrentPage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    /// <summary>The scan started by the latest drive selection; lets tests await it.</summary>
    public Task CurrentScan { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the drives and starts scanning the Windows drive.</summary>
    public void Initialize()
    {
        Drives.Clear();
        foreach (DriveSummary drive in _drives.GetDrives())
        {
            Drives.Add(new DriveViewModel(drive));
        }

        SelectedDrive = Drives.FirstOrDefault(d => string.Equals(d.Summary.RootPath, _drives.SystemDriveRoot, StringComparison.OrdinalIgnoreCase))
            ?? Drives.FirstOrDefault();
    }

    partial void OnSelectedDriveChanged(DriveViewModel? value)
    {
        if (value is not null)
        {
            CurrentScan = ShowDriveAsync(value, forceRescan: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private void Rescan()
    {
        if (SelectedDrive is { } drive)
        {
            CurrentScan = ShowDriveAsync(drive, forceRescan: true);
        }
    }

    private bool CanRescan() => !IsScanning;

    private async Task ShowDriveAsync(DriveViewModel drive, bool forceRescan)
    {
        _scanCancellation?.Cancel();

        if (!forceRescan && _summaries.TryGetValue(drive.Summary.RootPath, out var existing))
        {
            CurrentPage = existing;
            StatusText = string.Format(Strings.ScanComplete, existing.FileCount);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        StorageSummaryViewModel summary = null!;
        summary = new StorageSummaryViewModel(drive.Summary, _locations, node => OpenFolder(node, summary));
        _summaries[drive.Summary.RootPath] = summary;
        CurrentPage = summary;
        IsScanning = true;
        StatusText = string.Format(Strings.ScanningFiles, 0);

        var progress = new Progress<ScanProgress>(p =>
        {
            summary.ApplyProgress(p);
            if (_scanCancellation == cancellation)
            {
                StatusText = string.Format(Strings.ScanningFiles, p.FileCount);
            }
        });

        try
        {
            StorageTree tree = await _scanner.ScanAsync(drive.Summary.RootPath, _classifier, progress, cancellation.Token);
            summary.ApplyResult(tree);
            if (_scanCancellation == cancellation)
            {
                StatusText = string.Format(Strings.ScanComplete, tree.FileCount);
            }
        }
        catch (OperationCanceledException)
        {
            // Forget the partial result so the drive is scanned again next time.
            if (_summaries.GetValueOrDefault(drive.Summary.RootPath) == summary)
            {
                _summaries.Remove(drive.Summary.RootPath);
            }
        }
        finally
        {
            if (_scanCancellation == cancellation)
            {
                IsScanning = false;
            }
        }
    }

    private void OpenFolder(StorageNode node, StorageSummaryViewModel returnTo) =>
        CurrentPage = new FolderBrowserViewModel(node, _explorer, () => CurrentPage = returnTo);
}

public sealed class DriveViewModel(DriveSummary summary)
{
    public DriveSummary Summary { get; } = summary;

    public string Name
    {
        get
        {
            string label = string.IsNullOrWhiteSpace(Summary.Label) ? Strings.LocalDisk : Summary.Label;
            return $"{label} ({Summary.RootPath.TrimEnd('\\')})";
        }
    }

    public string UsedText => string.Format(Strings.DriveUsed, ByteSize.Format(Summary.UsedBytes), ByteSize.Format(Summary.TotalBytes));

    public double UsedFraction => Summary.TotalBytes == 0 ? 0 : (double)Summary.UsedBytes / Summary.TotalBytes;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/Pennyworth.App.Tests`
Expected: PASS, 19 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Pennyworth.App tests/Pennyworth.App.Tests
git commit -m "feat(app): add main window view model with drive switching and rescans"
```

---

### Task 11: Views, segmented bar and composition root

**Files:**
- Create: `src/Pennyworth.App/Controls/SegmentedBar.cs`
- Create: `src/Pennyworth.App/App.axaml`, `App.axaml.cs`
- Create: `src/Pennyworth.App/Views/MainWindow.axaml(.cs)`, `Views/StorageSummaryView.axaml(.cs)`, `Views/FolderBrowserView.axaml(.cs)`
- Modify: `src/Pennyworth.App/Program.cs` (replace the temporary entry point)

**Interfaces:**
- Consumes: every view model (Tasks 8–10), services (Task 7), `RuleClassifier` (Task 4), `KnowledgeBaseLoader` (Task 3), `DirectoryScanner` (Task 6), theme keys (Task 7).
- Produces: the runnable app. `SegmentedBar` control with `Segments : IReadOnlyList<BarSegment>?`, `TrackBrush : IBrush?`, `Radius : double`.

Views have no automated tests in M1 (Avalonia headless UI tests come with the final design in M6). Verification is the build (compiled bindings fail the build on any wrong property name) plus the manual checklist in Step 5.

- [ ] **Step 1: Add the segmented bar control**

`src/Pennyworth.App/Controls/SegmentedBar.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Controls;

/// <summary>
/// A horizontal bar split into coloured segments. Colours come from the theme resources
/// <c>Brush.Category.{ColorKey}</c>, so a new design only changes the tokens file.
/// </summary>
public sealed class SegmentedBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<BarSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentedBar, IReadOnlyList<BarSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<SegmentedBar, double>(nameof(Radius), 6);

    static SegmentedBar()
    {
        AffectsRender<SegmentedBar>(SegmentsProperty, TrackBrushProperty, RadiusProperty);
    }

    public IReadOnlyList<BarSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var clip = new RoundedRect(bounds, Radius);
        if (TrackBrush is { } track)
        {
            context.DrawRectangle(track, null, clip);
        }

        using (context.PushClip(clip))
        {
            double x = 0;
            foreach (BarSegment segment in Segments ?? [])
            {
                double width = segment.Fraction * bounds.Width;
                if (width <= 0)
                {
                    continue;
                }

                if (this.TryFindResource($"Brush.Category.{segment.ColorKey}", ActualThemeVariant, out object? resource) && resource is IBrush brush)
                {
                    context.DrawRectangle(brush, null, new Rect(x, 0, width, bounds.Height));
                }

                x += width;
            }
        }
    }
}
```

- [ ] **Step 2: Add the application and the composition root**

`src/Pennyworth.App/App.axaml`:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Pennyworth.App.ViewModels"
             xmlns:views="using:Pennyworth.App.Views"
             x:Class="Pennyworth.App.App"
             RequestedThemeVariant="Default">
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceInclude Source="avares://Pennyworth/Theme/Tokens.axaml" />
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>

  <Application.Styles>
    <SimpleTheme />
    <StyleInclude Source="avares://Pennyworth/Theme/Styles.axaml" />
  </Application.Styles>

  <Application.DataTemplates>
    <DataTemplate DataType="vm:StorageSummaryViewModel">
      <views:StorageSummaryView />
    </DataTemplate>
    <DataTemplate DataType="vm:FolderBrowserViewModel">
      <views:FolderBrowserView />
    </DataTemplate>
  </Application.DataTemplates>
</Application>
```

`src/Pennyworth.App/App.axaml.cs`:

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.App.Views;
using Pennyworth.Insights.Classification;
using Pennyworth.KnowledgeBase;
using Pennyworth.Scanning;

namespace Pennyworth.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: the only place concrete services are created.
            var classifier = new RuleClassifier(KnowledgeBaseLoader.LoadEmbedded().Locations);
            var viewModel = new MainWindowViewModel(
                new WindowsDriveService(),
                new DirectoryScanner(),
                classifier,
                new KnowledgeBaseLocationInfo(classifier),
                new ExplorerLauncher());

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            viewModel.Initialize();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
```

Replace `src/Pennyworth.App/Program.cs`:

```csharp
using Avalonia;

namespace Pennyworth.App;

internal static class Program
{
    // Don't use Avalonia types before AppMain runs: nothing is initialized yet.
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
```

- [ ] **Step 3: Add the views**

`src/Pennyworth.App/Views/MainWindow.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:Pennyworth.App.ViewModels"
        xmlns:res="using:Pennyworth.App.Resources"
        x:Class="Pennyworth.App.Views.MainWindow"
        x:DataType="vm:MainWindowViewModel"
        Title="{x:Static res:Strings.AppTitle}"
        Width="1100" Height="760" MinWidth="720" MinHeight="520"
        WindowStartupLocation="CenterScreen">
  <Grid RowDefinitions="Auto,*" Margin="{StaticResource Padding.Page}" RowSpacing="16">
    <Grid ColumnDefinitions="*,Auto" ColumnSpacing="12">
      <ListBox ItemsSource="{Binding Drives}" SelectedItem="{Binding SelectedDrive}">
        <ListBox.ItemsPanel>
          <ItemsPanelTemplate>
            <StackPanel Orientation="Horizontal" Spacing="8" />
          </ItemsPanelTemplate>
        </ListBox.ItemsPanel>
        <ListBox.ItemTemplate>
          <DataTemplate x:DataType="vm:DriveViewModel">
            <StackPanel Width="200" Spacing="6">
              <TextBlock Classes="heading" Text="{Binding Name}" />
              <ProgressBar Minimum="0" Maximum="1" Value="{Binding UsedFraction}" />
              <TextBlock Classes="caption" Text="{Binding UsedText}" />
            </StackPanel>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
      <StackPanel Grid.Column="1" VerticalAlignment="Center" Spacing="6" HorizontalAlignment="Right">
        <Button Content="{x:Static res:Strings.Rescan}" Command="{Binding RescanCommand}" HorizontalAlignment="Right" />
        <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
          <ProgressBar IsIndeterminate="True" Width="60" IsVisible="{Binding IsScanning}" VerticalAlignment="Center" />
          <TextBlock Classes="caption" Text="{Binding StatusText}" />
        </StackPanel>
      </StackPanel>
    </Grid>

    <ContentControl Grid.Row="1" Content="{Binding CurrentPage}" />
  </Grid>
</Window>
```

`src/Pennyworth.App/Views/MainWindow.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace Pennyworth.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
```

`src/Pennyworth.App/Views/StorageSummaryView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Pennyworth.App.ViewModels"
             xmlns:res="using:Pennyworth.App.Resources"
             xmlns:controls="using:Pennyworth.App.Controls"
             x:Class="Pennyworth.App.Views.StorageSummaryView"
             x:DataType="vm:StorageSummaryViewModel">
  <Grid RowDefinitions="Auto,Auto,Auto,*" RowSpacing="16">
    <StackPanel Spacing="4">
      <TextBlock Classes="title" Text="{x:Static res:Strings.StorageTitle}" />
      <TextBlock Classes="secondary" Text="{Binding UsedText}" />
    </StackPanel>

    <controls:SegmentedBar Grid.Row="1" Height="22" Radius="8"
                           Segments="{Binding Segments}"
                           TrackBrush="{DynamicResource Brush.Track}" />

    <TextBlock Grid.Row="2" Classes="warning" TextWrapping="Wrap"
               Text="{x:Static res:Strings.OvercountHint}"
               IsVisible="{Binding HasOvercount}" />

    <Grid Grid.Row="3" ColumnDefinitions="340,*" ColumnSpacing="16">
      <Border Classes="card">
        <Grid RowDefinitions="*,Auto,Auto" RowSpacing="12">
          <ListBox ItemsSource="{Binding Categories}" SelectedItem="{Binding SelectedCategory}">
            <ListBox.ItemTemplate>
              <DataTemplate x:DataType="vm:CategoryRowViewModel">
                <Grid ColumnDefinitions="Auto,*,Auto,Auto" ColumnSpacing="10">
                  <controls:SegmentedBar Width="12" Height="12" Radius="6" Segments="{Binding Swatch}" VerticalAlignment="Center" />
                  <TextBlock Grid.Column="1" Text="{Binding Name}" VerticalAlignment="Center" />
                  <TextBlock Grid.Column="2" Classes="caption" Text="{Binding PercentText}" VerticalAlignment="Center" />
                  <TextBlock Grid.Column="3" Text="{Binding SizeText}" FontWeight="SemiBold" MinWidth="70" TextAlignment="Right" VerticalAlignment="Center" />
                </Grid>
              </DataTemplate>
            </ListBox.ItemTemplate>
          </ListBox>

          <StackPanel Grid.Row="1" Spacing="4" Margin="12,0">
            <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="10">
              <controls:SegmentedBar Width="12" Height="12" Radius="6" Segments="{Binding Unaccounted.Swatch}" VerticalAlignment="Center" />
              <TextBlock Grid.Column="1" Text="{Binding Unaccounted.Name}" />
              <TextBlock Grid.Column="2" Text="{Binding Unaccounted.SizeText}" FontWeight="SemiBold" />
            </Grid>
            <TextBlock Classes="caption" TextWrapping="Wrap" Text="{x:Static res:Strings.UnaccountedHint}" />
          </StackPanel>

          <Button Grid.Row="2" HorizontalAlignment="Stretch" HorizontalContentAlignment="Center"
                  Content="{x:Static res:Strings.BrowseAllFolders}"
                  Command="{Binding BrowseAllCommand}" />
        </Grid>
      </Border>

      <Border Grid.Column="1" Classes="card">
        <Grid RowDefinitions="Auto,Auto,*" RowSpacing="8">
          <TextBlock Classes="heading" Text="{x:Static res:Strings.BiggestPlaces}" />
          <TextBlock Grid.Row="1" Classes="secondary" TextWrapping="Wrap"
                     Text="{x:Static res:Strings.BiggestPlacesPending}"
                     IsVisible="{Binding !IsComplete}" />
          <ScrollViewer Grid.Row="2">
            <ItemsControl ItemsSource="{Binding SelectedCategory.Areas}">
              <ItemsControl.ItemTemplate>
                <DataTemplate x:DataType="vm:CategoryAreaViewModel">
                  <Button Margin="0,0,0,8" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch"
                          Command="{Binding $parent[UserControl].((vm:StorageSummaryViewModel)DataContext).OpenAreaCommand}"
                          CommandParameter="{Binding}">
                    <Grid ColumnDefinitions="*,Auto" ColumnSpacing="12">
                      <StackPanel Spacing="2">
                        <TextBlock Classes="heading" Text="{Binding Title}" />
                        <TextBlock Classes="caption" Text="{Binding Path}" TextTrimming="CharacterEllipsis" />
                        <TextBlock Classes="secondary" Text="{Binding Description}" TextWrapping="Wrap"
                                   IsVisible="{Binding Description, Converter={x:Static ObjectConverters.IsNotNull}}" />
                      </StackPanel>
                      <TextBlock Grid.Column="1" Text="{Binding SizeText}" FontWeight="SemiBold" VerticalAlignment="Center" />
                    </Grid>
                  </Button>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
          </ScrollViewer>
        </Grid>
      </Border>
    </Grid>
  </Grid>
</UserControl>
```

`src/Pennyworth.App/Views/StorageSummaryView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace Pennyworth.App.Views;

public sealed partial class StorageSummaryView : UserControl
{
    public StorageSummaryView() => InitializeComponent();
}
```

`src/Pennyworth.App/Views/FolderBrowserView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Pennyworth.App.ViewModels"
             xmlns:res="using:Pennyworth.App.Resources"
             x:Class="Pennyworth.App.Views.FolderBrowserView"
             x:DataType="vm:FolderBrowserViewModel">
  <Grid RowDefinitions="Auto,Auto,*" RowSpacing="12">
    <Grid ColumnDefinitions="Auto,Auto,*,Auto" ColumnSpacing="8">
      <Button Content="{x:Static res:Strings.Back}" Command="{Binding BackCommand}" />
      <Button Grid.Column="1" Content="{x:Static res:Strings.Up}" Command="{Binding UpCommand}" />
      <ItemsControl Grid.Column="2" ItemsSource="{Binding Breadcrumbs}" VerticalAlignment="Center">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate>
            <WrapPanel />
          </ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
          <DataTemplate x:DataType="vm:BreadcrumbViewModel">
            <StackPanel Orientation="Horizontal">
              <Button Classes="link" Content="{Binding Name}"
                      Command="{Binding $parent[UserControl].((vm:FolderBrowserViewModel)DataContext).NavigateToCommand}"
                      CommandParameter="{Binding}" />
              <TextBlock Classes="caption" Text="›" VerticalAlignment="Center" />
            </StackPanel>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
      <TextBlock Grid.Column="3" Classes="heading" Text="{Binding SizeText}" VerticalAlignment="Center" />
    </Grid>

    <TextBlock Grid.Row="1" Classes="secondary" Text="{x:Static res:Strings.EmptyFolder}" IsVisible="{Binding IsEmpty}" />

    <Border Grid.Row="2" Classes="card" Padding="4">
      <ListBox ItemsSource="{Binding Items}">
        <ListBox.ItemTemplate>
          <DataTemplate x:DataType="vm:FolderItemViewModel">
            <Grid ColumnDefinitions="*,160,90,Auto" ColumnSpacing="12">
              <Button Classes="link" HorizontalAlignment="Left" Foreground="{DynamicResource Brush.TextPrimary}"
                      Command="{Binding $parent[UserControl].((vm:FolderBrowserViewModel)DataContext).OpenCommand}"
                      CommandParameter="{Binding}"
                      IsEnabled="{Binding IsDirectory}">
                <StackPanel Orientation="Horizontal" Spacing="8">
                  <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" />
                  <TextBlock Classes="caption warning" Text="{x:Static res:Strings.Locked}" IsVisible="{Binding IsLocked}"
                             ToolTip.Tip="{x:Static res:Strings.LockedHint}" />
                </StackPanel>
              </Button>
              <ProgressBar Grid.Column="1" Minimum="0" Maximum="1" Value="{Binding Fraction}" VerticalAlignment="Center" />
              <TextBlock Grid.Column="2" Text="{Binding SizeText}" TextAlignment="Right" VerticalAlignment="Center" />
              <Button Grid.Column="3" Classes="link" Content="{x:Static res:Strings.ShowInExplorer}"
                      Command="{Binding $parent[UserControl].((vm:FolderBrowserViewModel)DataContext).ShowInExplorerCommand}"
                      CommandParameter="{Binding}" />
            </Grid>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </Border>
  </Grid>
</UserControl>
```

`src/Pennyworth.App/Views/FolderBrowserView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace Pennyworth.App.Views;

public sealed partial class FolderBrowserView : UserControl
{
    public FolderBrowserView() => InitializeComponent();
}
```

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds with 0 warnings; all 87 tests pass (Core 15, KnowledgeBase 2, Insights 31, Scanning 20, App 19).

- [ ] **Step 5: Run the app and check it by hand**

Run: `dotnet run --project src/Pennyworth.App`

Check each item:
1. The window opens with every fixed/removable drive as a card (name, usage bar, "X used of Y"), and the Windows drive is selected.
2. While scanning, the status line counts files and the summary bar and category sizes grow live.
3. When the scan finishes, the bar's segments plus "Not measured yet" fill the whole bar, the largest category is selected, and "Biggest places" lists folders with friendly titles (e.g. "App data (AppData)") and descriptions.
4. Clicking a place opens the folder browser there; folders open on click, files don't; breadcrumbs and Up work; Back returns to the summary; "Show in Explorer" opens Explorer with the item selected.
5. Folders Windows refuses to read show "Locked".
6. Switching drives while a scan runs cancels it and scans the new drive; switching back to a finished drive is instant; "Scan again" rescans.
7. With Windows' display language set to Portuguese (Brazil), all text appears in Portuguese.
8. With Windows in dark mode, the app uses the dark tokens.

- [ ] **Step 6: Commit**

```bash
git add src/Pennyworth.App
git commit -m "feat(app): add storage summary and folder browser screens"
```
