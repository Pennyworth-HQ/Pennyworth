# Pennyworth v1 — Design Spec

**Date:** 2026-10-24
**Status:** Approved in brainstorming, pending spec review

## 1. Purpose

Windows tools leave beginners unable to answer "where did my disk space go?":
Explorer shows no folder sizes, Settings → Installed apps shows many programs
with no size, and hidden system space (restore points, hibernation file, etc.)
is never itemised, so the visible numbers never add up to the used space.

Pennyworth is a free, open-source Windows app that shows **where every byte
of storage goes, in plain language**, lets the user safely free space, and shows
which apps are using RAM and CPU right now. It must be **very fast** and usable
by someone with no technical knowledge.

### Success criteria

- The storage summary accounts for 100% of each drive's used space; any
  remainder is shown explicitly as "Unaccounted", never hidden.
- With admin unlocked, a full scan of a 1 TB NTFS drive completes in roughly
  5–15 seconds.
- The app window appears in under 1 second and shows the last scan immediately.
- Every installed app and game shows a true total size (install + data + caches).
- A beginner can free space without risk of breaking Windows or losing personal
  files.
- The app itself stays light (target: ≈60–120 MB RAM; near-zero CPU when the
  live monitor isn't on screen).

### Non-goals for v1

- Startup-apps manager, driver/update management, registry cleaning.
- Usage history over time / background service.
- AI-generated explanations. *(Amended 2026-10-26: an optional assistant, off until the user sets it up, ships as screen 07 — see `2026-10-25-ai-assistant-design.md`. The knowledge base stays the source of truth.)*
- Incremental scanning via the USN journal (candidate for v2; v1 uses saved
  snapshot + full rescan).
- Final visual design (comes later from Claude Design — see §10).

## 2. Decisions

| Topic | Decision |
|---|---|
| v1 scope | Storage analysis + cleanup, plus live RAM/CPU by app |
| Audience | Public release |
| Distribution | Free & open source (MIT), GitHub releases; winget later |
| Platforms | Windows 10 and 11, x64 (ARM64 when CI allows) |
| Drives | All internal and removable drives; fast mode on NTFS, normal scan elsewhere |
| Admin | Not required to start; "Unlock full scan" elevates a separate helper on demand |
| Scan speed | Raw MFT reading when admin is unlocked; directory walk otherwise |
| Explanations | Curated offline knowledge base (no AI, no network); optional assistant on top since 2026-10-26 (see the assistant spec) |
| First storage view | Plain-language category summary, drill down to folder tree |
| Actions | Explain + guided cleanup; personal files go to the Recycle Bin |
| Languages | English + Portuguese (Brazil) at launch, translation-ready |
| Stack | C# / .NET 10, Avalonia UI 12 (custom-styled, no native Windows look), CommunityToolkit.Mvvm, Native AOT |

Avalonia was chosen over WebView2/React and Blazor Hybrid for performance:
faster startup, lower memory, and the UI binds to scan results in-process
instead of serialising millions of nodes over a bridge.

## 3. Architecture

Two processes:

```
Pennyworth.exe (normal user)          Pennyworth.Helper.exe (admin, on demand)
  Avalonia UI + user-level work   <-->     MFT scan, system-space measurement,
  (normal scan, RAM/CPU, apps,   named      system-level cleanup.
   user cleanup)                  pipe      Started only on "Unlock full scan";
                                 (binary)   exits when idle.
```

The UI process **never** runs elevated.

### Projects

| Project | Responsibility | Depends on |
|---|---|---|
| `Pennyworth.Core` | Models (storage tree, categories, sizes, app identity), pipe protocol contracts. No Windows calls. | — |
| `Pennyworth.Scanning` | `IScanner` with two engines: `MftScanner` (admin, NTFS) and `DirectoryScanner` (no admin, any FS). Both produce the same `StorageTree`. Also system-space measurement and snapshot save/load. | Core |
| `Pennyworth.Insights` | Categorisation, knowledge-base lookup, installed-app discovery, true per-app size attribution, "cleanable" detection. | Core |
| `Pennyworth.KnowledgeBase` | Embedded data files (known paths, apps, games, cleanup rules, process explanations) in en + pt-BR, plus the loader. | Core |
| `Pennyworth.Monitoring` | Process snapshotting, CPU/RAM computation, grouping by app, process explanations. | Core, Insights |
| `Pennyworth.Cleanup` | Cleanup actions with preview, safety level, execution, history log. | Core, KnowledgeBase |
| `Pennyworth.Helper` | Admin executable hosting the admin-only parts of Scanning and Cleanup behind the pipe. | Scanning, Cleanup |
| `Pennyworth.App` | Avalonia UI, MVVM view models, theming tokens. No business logic. | all above (except Helper, launched as a process) |
| `*.Tests` | xUnit tests per module. | — |

Every library is marked `IsAotCompatible`; reflection-based serialisation is
avoided (source-generated `System.Text.Json` contexts, compiled bindings with
`x:DataType` everywhere).

## 4. Storage scanning

### Launch sequence

1. Show every drive's used/free bar immediately (`GetDiskFreeSpaceEx`).
2. Load the last saved snapshot from `%LocalAppData%\Pennyworth\` and show it,
   labelled with its age ("from 2 hours ago").
3. Start a `DirectoryScanner` scan of the system drive in the background; the
   tree and summary update live as results stream in.
4. Show the banner: "See everything, including hidden system space, in seconds
   → Unlock full scan". Clicking it triggers UAC and starts the helper, which
   rescans NTFS drives with `MftScanner`.

### `MftScanner` (helper, NTFS only)

- Opens the volume (`\\.\C:`), reads `$MFT` via its data runs, and parses FILE
  records directly: name(s), parent reference, attributes, `$DATA` sizes.
- Size accounting uses **allocated size on disk**; logical size is kept for
  display.
- Handles: hard links (bytes counted once; this is why WinSxS isn't as large as
  Explorer suggests), multiple names per record, compressed and sparse files,
  alternate data streams, attribute lists / extension records.
- A corrupt or unreadable record is skipped and counted, never fatal.
- Results stream to the UI over the pipe in a compact binary format (not JSON).

### `DirectoryScanner` (no admin, any file system)

- Parallel directory walk with `FindFirstFileEx` (large fetch, basic info),
  does not follow reparse points/junctions.
- Folders it can't read are recorded as **locked** with unknown size.

### Snapshots

The whole tree is saved after each scan in a compact binary file (one per drive)
so the next launch shows results instantly. The format is versioned; an
incompatible snapshot is discarded and the drive is rescanned.

### "Everything adds up to 100%"

For each drive: `used space − sum of scanned files` is broken down into:

- Restore points / volume shadow copies (shadow storage size)
- `hiberfil.sys`, `pagefile.sys`, `swapfile.sys`
- Windows reserved storage
- NTFS metadata (`$MFT`, `$LogFile`, etc.; measured by `MftScanner`)
- **Unaccounted**: whatever remains, always displayed

Without admin, items that can't be measured are shown as "Locked — unlock to
measure" instead of as numbers.

## 5. Insights (categories and per-app sizes)

### Categories

Every byte belongs to exactly one category, so the summary bar sums to 100%:

`Games · Apps · Your files · Windows · Hidden system · Other`

Rules come from the knowledge base (known paths and patterns) plus app
attribution. **Cleanable** is not a category, since it overlaps the others
(browser caches live inside Apps). It's shown as its own card: "You can free
23 GB safely", which links to the cleanup preview.

### Installed apps and games

Discovered from:

- Uninstall registry keys (HKLM, HKLM WOW6432Node, HKCU)
- Store / MSIX packages
- Game launchers: Steam (`libraryfolders.vdf` + app manifests), Epic,
  Xbox / Game Pass, EA app, Ubisoft Connect, Battle.net, GOG

Each app's **true size** = install folder + matched AppData (Local, LocalLow,
Roaming) + ProgramData + known cache locations. Matching uses knowledge-base
entries first, then publisher/product-name heuristics. Each attributed folder
carries a confidence level: **Sure** (knowledge base / registry path) or
**Likely** (name match), shown in the UI.

### Drill-down

Summary → category → app or folder → full folder tree, sorted largest first.
Every level offers "Open in Explorer".

## 6. Knowledge base

Embedded, offline, versioned data shipped with the app (and updated with app
releases). Entry types:

- **Known locations**: path pattern, category, plain-language title and
  description (en + pt-BR).
- **Known apps/games**: identity hints, data/cache folder patterns.
- **Cleanup rules**: id, path patterns or Windows mechanism, safety level,
  consequence text, removal method.
- **Process explanations**: executable or service name, friendly name,
  description, closable flag.

Unknown items are labelled by their owning app (if attributed) or by file type
("Video files", "Disk images").

## 7. Cleanup

Each cleanup item displays: what it is, current size, safety badge
(🟢 Safe · 🟡 Usually safe · 🔴 Think first), and what happens if removed.

| Item kind | Method |
|---|---|
| Temp files, caches, shader caches, crash dumps | Deleted directly (regenerable; the Recycle Bin would free nothing on the same drive) |
| Files the user picks in the folder tree | Sent to the Recycle Bin |
| Emptying the Recycle Bin | Separate action, shows its contents and asks for confirmation |
| Windows.old, update leftovers, restore points, hibernation file | Windows' official mechanisms, executed by the helper |
| Installed apps and games | "Uninstall" launches the app's registered uninstaller |

Hard rules, enforced in code:

- Never delete directly inside `C:\Windows`, `Program Files*`, or app install
  folders.
- Always show a preview first; only 🟢 items are pre-checked.
- Files in use are skipped and reported.
- Every run is appended to a local history log (what, when, bytes freed), shown
  on the History screen.
- A dry-run mode exists for tests and for computing the preview.

## 8. Live RAM / CPU

- One `NtQuerySystemInformation(SystemProcessInformation)` call per second
  collects all processes. Sampling runs **only while the screen is visible**.
- **Memory header:** used / total, split into In use, Compressed, Cached, with
  a plain-language note that cached memory is reclaimed instantly.
- **CPU header:** current total load + 60-second line.
- **Apps list:** processes grouped by app identity (executable path → installed
  app / Store package; shared identity logic with §5). Expand to see individual
  processes. Columns: name + icon, memory (private working set), CPU %.
- **Explained entries:** `svchost.exe` → "Windows services" (expanded to service
  names via service-to-PID mapping), `Vmmem`/`VmmemWSL` → "WSL / Docker
  virtual machine", `Memory Compression`, `System`, `Registry`, etc.
- **Actions:** "Close app" (graceful close first; "Force close" only after it
  refuses, with an unsaved-work warning). Critical processes are marked 🔒 and
  can't be closed. "See its storage" jumps to the app's storage detail.

## 9. Errors and security

### Degrade, never fail

- An unreadable folder counts as locked and the scan continues.
- If UAC is declined, the app stays in normal mode with a calm message.
- If the helper crashes, the normal-scan results stay on screen and the error is
  logged.
- A corrupt snapshot is discarded and the drive rescanned.
- Unexpected exceptions go to a local log file (`%LocalAppData%\Pennyworth\logs`);
  nothing is sent over the network.

### Helper security

- Named pipe ACL restricted to the launching user's SID; the helper accepts only
  the client process ID that launched it.
- Protocol is versioned; mismatched versions are rejected.
- The helper executes only **allowlisted cleanup rule IDs** from the knowledge
  base. It never accepts arbitrary paths to delete.
- The helper exits when the client disconnects or after an idle timeout.

## 10. UI and design handoff

v1 ships with a plain functional theme. Design flexibility is ensured by:

- One **design tokens** resource file (colours, typography, spacing, radii,
  elevation) for light and dark.
- Custom control themes/templates (no Fluent/Windows theme dependency), so a
  new visual design replaces styles without touching view models.

Screens to send to Claude Design:

1. **First run**: welcome, what the app does, privacy note (offline).
2. **Overview / Home**: drives, storage summary teaser, "free X GB" card, RAM/CPU teaser.
3. **Storage summary** (per drive): category bar, hidden-system breakdown, unlock banner.
4. **Category detail**: list of apps/folders in a category.
5. **Folder tree**: advanced drill-down, largest first.
6. **App detail**: true size breakdown with confidence, Uninstall, related process.
7. **Cleanup preview**: items with safety badges, consequences, checkboxes, total.
8. **History**: past cleanups.
9. **Live RAM/CPU**: memory and CPU headers, grouped apps list.
10. **Settings**: language, theme, drives to scan.
11. **"Unlock full scan" explainer**: why admin is needed, what it can see.

## 11. Localisation

- UI strings in `.resx` resources (en default, pt-BR).
- Knowledge-base entries carry both languages.
- Language follows Windows by default; switchable in Settings.
- Sizes and dates use culture-aware formatting.

## 12. Testing

- **Unit (xUnit), per module:** categorisation, app attribution, knowledge-base
  loading, process grouping (fake snapshots), cleanup in temp folders with
  dry-run, snapshot round-trip, pipe protocol serialisation.
- **MFT parser:** tests against recorded raw MFT samples from a small test disk
  image committed to the repo (records with hard links, ADS, sparse, compressed,
  attribute lists, corruption).
- **Integration (admin-only category):** `MftScanner` and `DirectoryScanner`
  report equal totals for a generated folder tree on a real NTFS volume.
- **Performance check:** scan-time and app-memory measurements recorded in CI
  on a fixed dataset to catch regressions.

## 13. Build and release

- GitHub repository, MIT licence, GitHub Actions CI (build + tests on every push).
- Native AOT publish of the App and Helper.
- **Velopack** installer with auto-update.
- Code signing via a free OSS programme (e.g. SignPath Foundation) so
  SmartScreen doesn't warn beginners.
- winget manifest after the first stable release.
