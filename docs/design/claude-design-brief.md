# Pennyworth — Design Brief for Claude Design

This document is everything needed to design the Pennyworth app. Hand it to Claude Design as-is.

## 1. The product in one paragraph

Pennyworth is a free, open-source Windows 10/11 desktop app that answers the question **"where did my disk space go?"** in plain language. Windows hides most of the answer: Explorer shows no folder sizes, Settings lists many programs with no size, and hidden system space (restore points, hibernation file, page file) is never itemized, so the numbers never add up. Pennyworth scans the drive, sorts every byte into a few understandable categories that **always add up to 100%**, explains what each big item is, and offers safe one-click cleanup. A second area shows which apps are using memory (RAM) and processor (CPU) right now.

## 2. Who it's for

- **Primary user: a beginner.** Someone who sees "Your disk is almost full" and has no idea why. Doesn't know what AppData, pagefile.sys or WinSxS are. Is afraid of deleting the wrong thing.
- **Secondary user: a curious power user.** Wants to drill down into the full folder tree and see exact numbers.
- Public release, English and Brazilian Portuguese at launch.

## 3. Design principles

1. **Plain language first.** Every number comes with a human explanation. "Hibernation file — saves your open apps when the PC hibernates" beats "hiberfil.sys".
2. **Everything adds up.** The storage bar always fills exactly 100% of used space. Space we can't see yet is shown honestly as "Not measured yet", never hidden.
3. **Calm, not scary.** No red alarms, no "Your PC is at risk!", no fake urgency. This is the opposite of shady "PC cleaner" apps. Trust is the product.
4. **Safety is visible.** Every cleanup item shows a safety level and what happens if you remove it, before anything is removed.
5. **Simple surface, depth on demand.** The first screen is a summary a beginner understands in 5 seconds. The full folder tree is always one click away for power users.
6. **Fast and light.** The app should *feel* instant: show something useful immediately, then fill in live while scanning.

## 4. Visual direction

- **Do not imitate Windows (Fluent / Windows 11 Settings) or macOS.** The owner wants an original, modern look. The app is built with Avalonia UI, which draws everything itself, so any visual style is possible.
- Modern, friendly, confident. Clean data visualization is the hero: the storage bar, category colors and size bars carry the experience.
- **Light and dark themes are both required.** Default follows Windows.
- You are free to choose typography, color palette, shapes, iconography and illustration style. See the technical constraints in §11 so the design is buildable.

## 5. Navigation and window

- Desktop window, resizable. **Default 1100 × 760 px, minimum 720 × 520 px.** Design at 1100 × 760 and show how key screens adapt at 720 px wide.
- Proposed main navigation (sidebar or other pattern; your choice):
  - **Home**
  - **Storage**
  - **Apps & games**
  - **Cleanup**
  - **Memory & CPU**
  - **History**
  - **Settings** (usually separated at the bottom)
- A drive switcher is needed in Storage (PCs often have C: plus a D: games drive).

## 6. Core concepts the visuals must express

### Storage categories (every byte belongs to exactly one)

| Category | Meaning (shown to users) | Example items |
|---|---|---|
| Games | Games and their data | Steam library, Epic Games, Xbox games |
| Apps | Installed programs and their data | Program Files, AppData (hidden app data), ProgramData |
| Your files | Your personal files | Documents, Pictures, Downloads, Desktop, Recycle Bin |
| Windows | Windows itself | C:\Windows, previous Windows installation |
| Hidden system | Space Windows uses behind the scenes | Hibernation file, page file, restore points |
| Other | Everything else | Unrecognized folders |
| *Not measured yet* | Used space the scan can't see yet | Protected areas, until "full scan" is unlocked |

Seven distinct colors are needed (6 categories + "Not measured yet"). They must be distinguishable for color-blind users and in both themes. Color must never be the only signal: always pair it with a label.

### Safety levels (cleanup)

| Level | Label | Meaning |
|---|---|---|
| 🟢 | **Safe** | Recreated automatically (caches, temp files). Pre-selected. |
| 🟡 | **Usually safe** | Fine for most people, small trade-off explained. Not pre-selected. |
| 🔴 | **Think first** | Has a real consequence (e.g. can't roll back a Windows update). Not pre-selected. |

### Confidence (per-app sizes)

When we credit a folder to an app: **Sure** (known for certain) or **Likely** (matched by name). Shown as a small tag.

### Scan modes

- **Normal scan**: runs without admin rights; takes from seconds to a few minutes; some areas show as "Locked".
- **Full scan**: user clicks **"Unlock full scan"** → Windows asks for permission → scan takes seconds and measures hidden system space exactly. This is a key moment and needs an explainer (screen 11).

### Locked

A folder Windows didn't allow us to read. Shown with a small "Locked" badge and a tooltip/explanation; never an error screen.

## 7. Screens

Use the realistic sample data in §9. Each screen lists its content, actions and the states to design.

### Screen 1 — First run (welcome)

- Welcome message and 3 short points: *See where your space goes · Free space safely · See what's slowing your PC*.
- Privacy note: "Everything stays on your PC. No account, no internet needed."
- Language picker (System default / English / Português).
- Primary button: **"Analyze my PC"**.

### Screen 2 — Home (overview)

- Greeting/headline: "Your PC at a glance".
- **Drive cards**: name ("Local Disk (C:)"), usage bar, "877 GB used of 953 GB", warning tone when > 90% full.
- **Storage teaser**: mini category bar for the main drive + the biggest category ("Apps use 413 GB").
- **"You can free 23 GB safely"** card → opens Cleanup.
- **Memory & CPU teaser**: "Memory 11.2 of 16 GB · CPU 8%" + top app.
- **Unlock banner** (only if not unlocked): "See everything, including hidden system space, in seconds → Unlock full scan".

### Screen 3 — Storage summary (main screen)

- Drive switcher.
- Title "Where is my space?" + "877 GB used of 953 GB".
- **Large segmented storage bar** (the hero visual).
- **Category legend list**: color, name, percent, size. Selecting a category shows its biggest places.
- **"Not measured yet"** row with a short explanation.
- **Hidden system breakdown** (after full scan): Restore points, Hibernation file, Page file, Swap file, Windows reserved storage, NTFS file system data, Unaccounted.
- **"Biggest places"** panel for the selected category: friendly title, path (small), one-line description, size. Click → category detail / app detail / folder browser.
- "Browse all folders" button.
- **States to design:**
  - *Scanning*: live counter ("Scanning… 1,552,450 files found"), bar and numbers grow live; biggest places show "appear when the scan finishes".
  - *Complete*.
  - *Showing saved results*: "From 2 hours ago" + "Scan again".
  - *Overcount note*: "Some files were counted more than once (hard links), so totals are a little high. The full scan will show exact numbers."
  - *Unlocked* vs *not unlocked* (banner).

### Screen 4 — Category detail (e.g. Games)

- Header: category name, color, total, share of drive.
- List of items in the category: games/apps (with icon when known) or folders; title, description, size, share bar.
- Click a known app → Screen 6; click a folder → Screen 5.

### Screen 5 — Folder browser (advanced)

- Back to summary, Up, **breadcrumbs** (C:\ › Users › User › AppData).
- Current folder total.
- Rows sorted largest first: folder/file icon, name, **size bar relative to the current folder**, size, "Locked" badge where applicable, **"Show in Explorer"** action (can appear on hover/selection, but must also be keyboard accessible).
- Folders open on click; files don't.
- Must handle very long lists (thousands of rows) and long names (truncate with ellipsis).
- States: empty folder, locked folder.

### Screen 6 — App detail

- App icon, name, publisher.
- **True total size** (big number) vs what Windows Settings shows ("Windows shows: —").
- Breakdown rows: Install folder, App data, Caches, Shared data — each with size and a **Sure/Likely** tag.
- Actions: **Uninstall** (opens the app's official uninstaller), Open folder, "Running now: uses 1.2 GB of memory" → Memory & CPU.

### Screen 7 — Cleanup preview

- Headline: **"You can free 23.4 GB"**.
- Items grouped or sorted by safety: checkbox, title ("Temporary files"), plain description, **what happens if removed** ("Browsers will load sites a little slower the first time"), size, safety badge.
- Only 🟢 items pre-checked. Running total of selected items.
- Primary button: **"Clean 18.2 GB"**.
- **States:** confirmation step, cleaning in progress (per-item progress), result ("Freed 18.2 GB · 3 files skipped because they're in use"), nothing to clean (empty state).
- Special item: **Empty Recycle Bin** shows its contents summary and always asks for confirmation.

### Screen 8 — History

- List of past cleanups: date, amount freed, number of items; expandable to see what was removed.
- Empty state for a new install.

### Screen 9 — Memory & CPU (live)

- **Memory header**: used / total and a bar split into *In use*, *Compressed*, *Cached*, with a friendly note: "Cached memory isn't wasted — Windows hands it back instantly when an app needs it."
- **CPU header**: current % + small 60-second line chart.
- **Apps list, grouped by app** (e.g. 30 Chrome processes → one row "Google Chrome · 2.1 GB · 4%"): icon, name, memory, CPU, expand to individual processes.
- Explained system entries: "Windows services" (expands to service names), "WSL / Docker virtual machine", "Memory Compression" — with a short description.
- Critical Windows processes show a 🔒 lock and can't be closed.
- Actions: **Close app** (confirm), **Force close** only after the app refuses (warning about unsaved work), **See its storage**.
- Numbers update every second: design so values changing don't make the layout jump (tabular numbers, fixed column widths).

### Screen 10 — Settings

- Language: System default / English / Português (Brasil).
- Theme: System / Light / Dark.
- Drives to scan: toggles per drive.
- About: version, "Free and open source", link to GitHub, privacy statement.

### Screen 11 — "Unlock full scan" explainer (dialog)

- Why: "To measure hidden system space and scan in seconds, Pennyworth needs to read the drive directly. Windows will ask for your permission."
- What it can see (restore points, protected folders) and what it **won't** do ("It never deletes anything without showing you first").
- Buttons: **"Unlock"** (Windows permission prompt follows), "Not now".
- **Declined state**: calm message, "You can unlock anytime", app keeps working in normal mode.

## 8. Component inventory

Please design these as reusable components with their states (default, hover, pressed, focused, disabled where relevant), in light and dark:

- Segmented storage bar (large hero version + small inline version)
- Category legend row (color dot, name, %, size; selected state)
- Drive card (normal, almost full, selected)
- Size/share bar for list rows
- List row: folder/file/app (with icon, title, subtitle, size, actions, locked badge)
- Expandable group row (Memory & CPU apps)
- Breadcrumbs
- Safety badge (Safe / Usually safe / Think first)
- Confidence tag (Sure / Likely)
- "Locked" badge with tooltip
- Banner (info, warning) — e.g. unlock banner, overcount note
- Cards (default, highlighted call-to-action like "You can free 23 GB")
- Buttons: primary, secondary, link/text, danger
- Checkbox, toggle switch, radio/segmented choice
- Progress: determinate, indeterminate, "scanning…" live counter
- Small line chart (CPU 60 s)
- Dialog / confirmation
- Toast / inline result message
- Empty states (no history, empty folder, nothing to clean)
- Navigation (sidebar or chosen pattern) with selected state
- Tooltip

## 9. Realistic sample data (use in mockups)

From a real scan of the owner's PC:

**Local Disk (C:)** — 953 GB total, 877 GB used

| Category | Size | Biggest places |
|---|---|---|
| Games | 201 GB | Steam games 192 GB · Epic Games 6.8 GB · Xbox games 2.0 GB |
| Apps | 412 GB | Installed programs (32-bit) 168 GB · App data (AppData) 148 GB · Installed programs 74.2 GB |
| Your files | 57.5 GB | User folder 55.8 GB · Recycle Bin 1.65 GB |
| Windows | 49.5 GB | Windows system files 48.9 GB · Windows upgrade files 669 MB |
| Hidden system | 23.4 GB | Page file 17.0 GB · Hibernation file 6.38 GB · Swap file 16 MB |
| Other | 114 GB | Unrecognized folders in C:\ |
| Not measured yet | 18.7 GB | — |

**Novo volume (D:)** — 1.36 TB total, 1.08 TB used.

Files scanned on C: about 1,747,000.

The two examples below are illustrative (not measured yet), for mockups only.

Example app detail: *Docker Desktop* — Install folder 2.1 GB (Sure), App data 14.4 GB (Sure), Caches 1.3 GB (Likely).

Example cleanup items: Temporary files 3.2 GB 🟢 · Browser caches 2.8 GB 🟢 · Shader caches 1.9 GB 🟢 · Crash dumps 640 MB 🟢 · Windows update leftovers 5.1 GB 🟡 · Recycle Bin 1.65 GB 🟡 · Previous Windows installation 7.9 GB 🔴.

## 10. Copy and localization

- **Tone:** friendly, calm, short sentences, no jargon. Technical names (e.g. `hiberfil.sys`) appear only as small secondary text.
- **Portuguese (Brazil) text is up to ~35% longer** than English. Layouts must tolerate it (no fixed-width buttons that truncate labels).
- Number format follows the language: English "1.5 GB", Portuguese "1,5 GB". Sizes use binary units like Windows (1 KB = 1024 bytes) with labels B, KB, MB, GB, TB.
- Sample PT-BR strings: "Para onde foi meu espaço?" · "Seus arquivos" · "Sistema oculto" · "Ainda não medido" · "Ver todas as pastas" · "Mostrar no Explorador" · "Verificar de novo".

## 11. Technical constraints (so the design can be built faithfully)

The app is native (Avalonia UI on .NET), not a website.

- **Supported well:** solid colors, linear/radial gradients, rounded corners, borders, box shadows, opacity, custom fonts, SVG-style vector icons, transitions and simple animations (fade, slide, scale, color, bar growth), custom-drawn charts and bars.
- **Use carefully:** blur/frosted-glass effects (only reliable as the whole window background on Windows 11; not behind individual cards); heavy per-row animation in lists with thousands of rows.
- **Fonts:** must have an open license we can bundle (e.g. Inter, Geist, Plus Jakarta Sans, IBM Plex). Numbers should support tabular figures.
- **Icons:** an open-license icon set (e.g. Lucide, Phosphor, Tabler). App icons come from the installed programs themselves.
- **Accessibility:** WCAG AA contrast in both themes, visible keyboard focus on everything clickable, nothing available only on hover, works at 100–200% Windows display scaling.

## 12. Design tokens (required deliverable)

The app is already wired to a tokens file; the design is applied by replacing token values and control styles. Please deliver a token table (light and dark values) covering at least these keys, and add any new tokens your design needs (e.g. shadows, extra radii, spacing scale, font family):

| Token key | Purpose |
|---|---|
| `Brush.Background` | Window background |
| `Brush.Surface` | Cards and panels |
| `Brush.SurfaceHover` | Hover/selected surfaces |
| `Brush.Border` | Card and control borders |
| `Brush.Track` | Empty part of bars |
| `Brush.TextPrimary` | Main text |
| `Brush.TextSecondary` | Secondary text |
| `Brush.Accent` | Primary actions, links, focus |
| `Brush.Warning` | Warnings, notes |
| `Brush.Category.Games` | Category color |
| `Brush.Category.Apps` | Category color |
| `Brush.Category.YourFiles` | Category color |
| `Brush.Category.Windows` | Category color |
| `Brush.Category.HiddenSystem` | Category color |
| `Brush.Category.Other` | Category color |
| `Brush.Category.Unaccounted` | "Not measured yet" color |
| `FontSize.Title` / `Heading` / `Body` / `Caption` | Type scale |
| `Radius.Card` / `Radius.Control` | Corner radii |
| `Padding.Page` / `Padding.Card` / `Padding.Row` | Spacing |

Likely additions: `Brush.Danger`, `Brush.Success`, `Brush.Safety.Safe` / `UsuallySafe` / `ThinkFirst`, `Shadow.Card`, `FontFamily.Base`, spacing scale.

## 13. What to deliver

1. All 11 screens at 1100 × 760, **light and dark**, using the sample data.
2. Key screens (Storage summary, Folder browser, Memory & CPU) at **720 px** wide.
3. The important states listed per screen (scanning, empty, locked, declined, confirmation, result).
4. The component inventory (§8) with states.
5. The token table (§12) with light and dark values.
6. HTML/CSS export of the screens is welcome; it will be translated into the native app.
