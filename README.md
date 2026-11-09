# Pennyworth

A free Windows app that explains your PC in plain language, in English and Portuguese (Brazil):
where every byte of storage goes, what can be cleaned safely (and exactly which files), which
apps are using memory and the processor right now, every part of the hardware, and — if you
want — a discreet butler who answers questions about your own PC.

## What it does

| Screen | What you get |
|---|---|
| 01 Home | The state of the PC at a glance |
| 02 Storage | Where the space on each drive goes, by category, always adding up to 100% |
| 03 Cleanup | What can be freed, grouped by how safe it is; every file listed before anything is deleted |
| 04 Memory & CPU | Apps using memory and the processor right now, with plain names for Windows processes |
| 05 Folders | A folder browser sorted by size |
| 06 Specs | Every part of the PC in plain words, with "Copy all" |
| 07 Ask | The assistant: answers from what Pennyworth measured, never changes anything by itself |
| 08 Stress test | Loads processor, memory and disk like OCCT, Prime95 and HeavyLoad, checks every result, and shows a load radar, dials, stress level, power and temperature |
| 3D | An ASCII PC you can rotate, with live status per part |

## The assistant (07 Ask)

- **Runs on your PC** with a small model downloaded on request (Phi-4-mini on graphics cards
  with 6 GB+ of video memory, Phi-3.5-mini on the processor otherwise; MIT licence, files
  checked against pinned SHA-256 hashes), **or uses your own AI account**: any
  OpenAI-compatible endpoint, Anthropic, Google Gemini or Azure OpenAI.
- **Grounded:** the app measures the facts and applies its own diagnosis rules; the model only
  explains them. It can point to Pennyworth's screens, never act on the PC.
- **Private:** with an AI account, you see exactly what will be sent before it goes; your
  name, your user folder and personal folders are replaced by tags such as `<user>`. Keys live
  in the Windows Credential Manager. Conversations stay on the PC and can be deleted.
- **Safe:** advice such as editing the registry or disabling Windows Security is replaced
  before you see it.
- Nothing about the assistant runs, downloads or connects until you set it up.

Design and decisions: `docs/superpowers/specs/2026-10-25-ai-assistant-design.md`.

## Build and run

The .NET SDK is pinned in `global.json` (10.0.401). The same two commands work on both systems:

```bash
dotnet run --project src/Pennyworth.App
dotnet test --solution Pennyworth.slnx
```

### Windows

Requirements: Windows 10/11 x64.

Native AOT publish (needs the Visual Studio C++ build tools):

```bash
dotnet publish src/Pennyworth.App -c Release -r win-x64 -p:PublishAot=true -o publish
```

### Linux

Requirements: x64 Linux with glibc 2.27+ and an X11 or Wayland session; `libicu` and `libfontconfig`
(present on every common desktop). `libsecret-1` is optional: without it Pennyworth starts, says in one
sentence that API keys cannot be saved, and turns off only the keyring.

The window uses Wayland natively inside a Wayland session and X11 otherwise;
`PENNYWORTH_DISPLAY=x11` keeps X11 (XWayland) inside a Wayland session.

Release artefacts (a self-contained tarball and an AppImage, no .NET needed on the target machine):

```bash
tools/package/build-linux.sh --version 0.1.0     # writes artifacts/linux-x64/
tools/package/smoke-linux.sh --tarball artifacts/linux-x64/pennyworth-0.1.0-linux-x64.tar.gz
```

The script publishes `linux-x64` self-contained, lays the result out as `bin/`, `lib/pennyworth/` and `share/`
(desktop file and `hicolor` icons), and builds the AppImage with a pinned, checksum-verified `appimagetool`
(downloaded on first use). `--aot` publishes the app with Native AOT instead (needs clang and zlib headers),
`--no-appimage` stops after the tarball and the AppDir. The administrator helper (below) is always published with
Native AOT, whatever `--aot` says, so it needs no runtime next to it; that needs clang and zlib headers too, and
`--no-helper` builds without it on a host that has neither (the tarball then has no helper, no policy and no
`install-helper.sh`, and its README says so). The tarball is sorted, owned by root and stamped with the commit time;
the helper is built with `ContinuousIntegrationBuild`, but bit-identical rebuilds also depend on the C toolchain and
are not guaranteed. The smoke script starts a package with an empty
environment and no SDK in a private Xvfb (`--display wayland`: a private headless sway, no X server) and
checks that the window opened, that its class matches the desktop file, and that something was drawn.

#### Administrator access (optional, opt-in)

Pennyworth runs as you. On Linux four things need more than that: memory module details, the full system space
(snapshots and file system bookkeeping), the size of folders your account cannot open (`/root`...), and cleaning the
root-owned package caches and crash reports. They share **one** concept in the window, *Administrator access*
(header: Off, Active with a Lock button, or Needs setup): one explained password prompt (polkit, the usual `pkexec`
dialog) turns it on, and after that every one of the four works without asking again. Pennyworth never asks for the
password itself, never sees it, and never types one.

- **Turning it on** starts one long-lived helper (`pennyworth-helper serve`) through a single `pkexec`. It ends when you
  press Lock, after 15 minutes without a request (`HelperProtocol.IdleTimeout`; then the next action prompts again),
  and when Pennyworth closes (its input pipe closes). A dismissed prompt is never retried by itself; a session that
  died is restarted only by your next action.
- **When it becomes active:** the folders the scan could not read are measured and shown in the Storage tree (named
  "Measured with administrator access: /root, ...") so the totals add up, the system space includes the
  administrator-only parts, the memory modules fill in and the root-owned cleanup rules can be selected. A scan that
  finishes while it is on does the same. Cleanup still shows its preview and still needs your confirmation.
- **Needs setup** means the helper is not installed (or not trustworthy, or older than the app). The app then offers
  **Set up administrator access**: a dialog says exactly which two files it will install and where (the helper to
  `/usr/libexec/pennyworth/`, its polkit policy to `/usr/share/polkit-1/actions/`, both from this copy of the app, with
  their SHA-256), then one `pkexec` copies them. Locked folders and root cleanup say they need it and offer it; memory
  modules and system space still work without it as one-off prompts (fixed system tools through `pkexec`) and say so.

```bash
# What the app's setup does, by hand (needs the tarball; the AppImage does not ship the helper):
tar -xzf pennyworth-0.1.0-linux-x64.tar.gz && cd pennyworth-0.1.0-linux-x64
sudo ./install-helper.sh
# Remove it again:
sudo ./install-helper.sh --uninstall     # or: sudo rm /usr/libexec/pennyworth/pennyworth-helper /usr/share/polkit-1/actions/app.pennyworth.policy
```

From a checkout, publish `src/Pennyworth.Helper` with Native AOT and pass it: `sudo tools/package/linux/install-helper.sh --helper <published pennyworth-helper>`.
The AppImage does not ship the helper (an AppImage runs from a FUSE mount, which is not a trusted root-owned path);
a helper installed from the tarball is used by any build of the app.

Why it is built this way, and what remains risky:

- **Trusted path only.** The app runs the helper only from `/usr/libexec/pennyworth`, and only if the file and every
  folder above it are owned by root, not writable by group or others, and not symbolic links (checked without
  following links). Otherwise it does not use it. Nothing else is ever run as root, except the one-time setup below.
- **A fixed list of operations, nothing generic.** A session answers exactly `read-firmware-tables`,
  `measure-system-space`, `measure-folders` and `clean-system <rule>` (the same code the one-shot subcommands run),
  as length-prefixed JSON frames (version-checked, size-capped) on stdin/stdout, one request at a time, each limited
  to 10 minutes. No shell, no command, no "read this file", no path to delete: cleanup uses only the compiled-in
  folders of the rule and deletes a listed file only if it is still the same regular file (or link) from the preview,
  without following symbolic links or crossing file systems.
- **It serves one peer.** It opens no socket, file or FIFO. As root it checks that `PKEXEC_UID` is an ordinary user,
  that standard input and output are pipes, and that its parent process (pkexec replaces itself with the helper, so
  that is the app) belongs to that user; it stops if the parent changes. It reads no environment but `PKEXEC_UID`.
  *Residual risk:* while a session is active, a process running as the same user that can control the app (for example
  through `ptrace`) could ask the session for those four operations, including deleting the package caches and crash
  reports listed by a preview it supplies, without another prompt. That is what "ask once" means; Lock ends it.
- **The one-time setup runs a fixed script as root.** The text of `tools/package/linux/install-helper.sh` is compiled
  into the app and run as `pkexec bash -c <script>`, never a script file from a folder you can write. It copies the
  two files to root-owned temporary names, refuses links and a helper that is not an ELF program, checks the copies
  against the SHA-256 the dialog showed, and only then moves both into place (both or neither). *Residual risk:* the
  files come from the app's own folder, which you can write; something that already runs as you could have replaced
  them before the dialog read them, and the fingerprint would then be the replacement's. You are authorising an
  install of the app's own files: compare the fingerprints with the release page if that matters to you.
- **One polkit action per subcommand.** `app.pennyworth.policy` has an action for `serve` (the app's one prompt: a single short
  sentence, as for every action, English and Brazilian Portuguese; what it can do, the idle time and Lock are said by the
  app's own "Turn on administrator access?" dialog that comes first; `auth_admin_keep`; the session, not polkit's
  authorisation cache, is what avoids re-prompting) and for each one-shot subcommand (for running a single operation by
  hand with `pkexec`; the cleanup one asks every time). Inactive and remote sessions are refused.
- **Windows is unchanged.** Windows keeps its own elevation; none of this applies there.

This is a deliberate, explicit exception to "Pennyworth never elevates", approved for these four features only.
The Linux port plan (`docs/design/2026-10-26-linux-port-plan.md`) is not part of this branch, so this section is the
decision record for the exception.

The launcher icons are generated from the brand sources:
`python -I tools/brand/build_logo_assets.py docs/design/brand src/Pennyworth.App/Assets tools/package/linux/icons`
(needs Pillow).

CI (`.github/workflows/`): `linux.yml` builds, tests, packages, and launches the packages under Xvfb and under a
Wayland compositor, keeping the screenshots as artifacts; `windows.yml` builds and tests on Windows.

## Projects

| Project | Responsibility |
|---|---|
| `Pennyworth.Core` | Storage tree, categories, formatting |
| `Pennyworth.KnowledgeBase` | Bilingual data, one set per operating system (`Data/` for Windows, `Data/linux/` for Linux, picked by `IPlatform.Name`): known locations, processes, cleanup rules, diagnosis rules, blocked advice |
| `Pennyworth.Insights` | Path classification |
| `Pennyworth.Scanning` | Drive scanning |
| `Pennyworth.Cleanup` | Measuring and cleaning, Recycle Bin |
| `Pennyworth.Platform.Linux.Helper` | Dependency-free library the app and the root helper share: the helper protocol, mount table, `statx`, system-space parsers (Linux only) |
| `Pennyworth.Helper` | The `pennyworth-helper` executable, the only program that runs as root; references nothing but the library above (Linux only, optional) |
| `Pennyworth.Monitoring` | Live memory/CPU/GPU, power and temperature (when Windows reports them) and hardware specs |
| `Pennyworth.Stress` | Stress test workloads (processor, memory, disk) with result verification |
| `Pennyworth.Assistant` | Facts, diagnosis, context, persona prompt, action links, advice filter, anonymiser, session, history, usage |
| `Pennyworth.Assistant.Providers` | Cloud chat clients, API keys in the Credential Manager, test connection |
| `Pennyworth.Assistant.Local` | Model catalogue, hardware check, verified download, on-PC ONNX model |
| `Pennyworth.App` | Avalonia UI |
| `tools/AssistantEval` | Evaluation harness for the assistant (not shipped) |

Fonts: Golos Text and JetBrains Mono, SIL Open Font License 1.1 (licences ship with the app).
