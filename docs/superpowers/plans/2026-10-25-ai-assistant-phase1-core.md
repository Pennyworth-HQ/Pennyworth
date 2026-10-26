# AI Assistant Phase 1: Core (`Pennyworth.Assistant`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Write the failing test first, then the code, run the tests, commit.

**Goal:** A small, AOT-clean, fully unit-tested library that turns what Pennyworth measured into facts and findings, builds a bounded context per topic, composes the butler prompt, streams an answer from any `IChatClient`, keeps unsafe advice and invalid action links out of it, anonymises what goes to the cloud, and stores conversations and token usage locally.

**Architecture:** `Pennyworth.Assistant` depends on `Core`, `Monitoring`, `Cleanup`, `KnowledgeBase` and `Microsoft.Extensions.AI.Abstractions` only — no provider SDK, no `HttpClient`, no ONNX runtime. The app (phase 3) hands it a `PcSnapshot`; the library never reads Windows itself. Diagnosis rules and blocked-advice patterns are bilingual data in `Pennyworth.KnowledgeBase`, like the cleanup rules.

**Spec:** `docs/superpowers/specs/2026-10-25-ai-assistant-design.md` (§4–§8, §12, §13, §17)
**Parent plan:** `docs/superpowers/plans/2026-10-25-ai-assistant.md` (phase 1)

## Global Constraints

- Everything in the M1 plan's Global Constraints (net10.0-windows, central package versions, `Nullable`, `TreatWarningsAsErrors`, `IsAotCompatible`, source-generated JSON only).
- `Microsoft.Extensions.AI.Abstractions` 10.11.0 is the only new package in `src/`.
- Regexes from data (blocked advice) are compiled at runtime with `RegexOptions.CultureInvariant | IgnoreCase` and a 100 ms match timeout; fixed regexes use `[GeneratedRegex]`.
- User-facing text produced by the library is bilingual through `LocalizedText` or the culture passed in; the library never reads `CultureInfo.CurrentUICulture` itself.
- Tests: `tests/Pennyworth.Assistant.Tests` (xUnit v3), `TestContext.Current.CancellationToken` for async calls, a `FakeChatClient` for every model interaction.
- Commits: one per task (or smaller), under 150 characters, author AnGabSS only, no co-author line. Push after each task group.

## File Structure

```
src/Pennyworth.KnowledgeBase/
  Data/diagnosis.json               Bilingual diagnosis rules (conditions over facts → finding + action)
  Data/advice.json                  Blocked-advice patterns per language + replacement line
  AssistantCatalogModels.cs         DiagnosisRule, FactCondition, BlockedAdvice, catalogs, JSON context
  KnowledgeBaseLoader.cs            + LoadDiagnosisRules(), LoadBlockedAdvice()
src/Pennyworth.Assistant/
  Pennyworth.Assistant.csproj
  AssistantProfile.cs               Backend kind, model, endpoint, cap (no key)
  AssistantTopic.cs                 What a question is about (overview, storage, memory, cleanup item, spec section, part)
  Snapshot/PcSnapshot.cs            Input from the app: specs, recent monitor samples, drives, categories, cleanup, unlock
  Facts/Fact.cs                     Id, group, label, kind, number, text; Format(culture)
  Facts/FactLabels.cs               Bilingual labels for every fact id
  Facts/FactCollector.cs            PcSnapshot → IReadOnlyList<Fact>
  Diagnosis/Finding.cs              Rule id, severity, text, action
  Diagnosis/DiagnosisEngine.cs      Evaluates rules over facts
  Context/ContextBundle.cs          Facts + findings + allowed actions for one topic, as sent
  Context/ContextBuilder.cs         Topic selection, ordering, character budget
  Prompts/PersonaPrompt.cs          System prompt EN/PT-BR from a bundle
  Actions/AssistantAction.cs        Action kinds and arguments; token text
  Actions/ActionLinker.cs           Parses, validates and strips [[action:…]] tokens (also from a stream)
  Safety/AdviceFilter.cs            Sentence-level, refusal-aware filter; streaming
  Privacy/Anonymizer.cs             Placeholders for user, profile path, machine, e-mails, personal folders; restore
  Session/AssistantUpdate.cs        Streamed updates: text, action, blocked, usage, done
  Session/AssistantSession.cs       Question → context → prompt → IChatClient → filter/link/restore → updates
  History/ConversationStore.cs      One JSON Lines file per conversation; list/load/append/feedback/delete all
  Usage/UsageMeter.cs               Tokens per profile per month, cap state
tests/Pennyworth.Assistant.Tests/
  FakeChatClient.cs, Samples.cs and one test file per unit above
tools/AssistantEval/                Uses PersonaPrompt + ContextBundle instead of its provisional prompt; 30+ scenarios
```

## Key types (signatures the tasks implement)

```csharp
// AssistantTopic.cs
public enum TopicKind { Overview, Storage, Memory, Processor, Graphics, Power, CleanupItem, SpecSection }
public sealed record AssistantTopic(TopicKind Kind, string? Key = null)   // Key: rule id or spec section id
{ public static AssistantTopic Overview { get; } = new(TopicKind.Overview); }

// AssistantProfile.cs
public enum AssistantBackend { Local, OpenAiCompatible, Anthropic, Gemini, AzureOpenAi }
public sealed record AssistantProfile(string Id, AssistantBackend Backend, string DisplayName, string ModelId,
    string? Endpoint = null, long? MonthlyTokenCap = null) { public bool IsLocal => Backend == AssistantBackend.Local; }

// Snapshot/PcSnapshot.cs
public sealed record DriveFact(string Root, string? Label, long TotalBytes, long FreeBytes, bool IsSystem);
public sealed record PcSnapshot(
    PcSpecs? Specs,
    IReadOnlyList<MonitorSample> RecentSamples,          // oldest first, about one per second
    IReadOnlyList<DriveFact> Drives,
    IReadOnlyDictionary<StorageCategory, long>? SystemDriveCategories,
    IReadOnlyList<CleanupEstimate> Cleanup,
    bool IsUnlocked);

// Facts/Fact.cs
public enum FactKind { Bytes, Percent, Count, Text }
public sealed record Fact(string Id, TopicKind Group, LocalizedText Label, FactKind Kind, double Number, string? Text = null)
{ public string Format(CultureInfo culture); }

// Diagnosis
public enum Severity { Info, Warning, Problem }
public sealed record Finding(string RuleId, Severity Severity, string Text, AssistantAction? Action, TopicKind Group);

// Context/ContextBundle.cs
public sealed record ContextLine(string Label, string Value);
public sealed record ContextBundle(AssistantTopic Topic, CultureInfo Culture,
    IReadOnlyList<ContextLine> Facts, IReadOnlyList<string> Findings, IReadOnlyList<AssistantAction> AllowedActions)
{ public string ToText(); }   // exactly what goes into the prompt; also what the send preview shows

// Actions/AssistantAction.cs
public enum ActionKind { Cleanup, CleanupItem, Memory, CloseApp, Storage, Folders, Specs, Unlock }
public sealed record AssistantAction(ActionKind Kind, string? Argument = null)
{ public string Token { get; }  public static AssistantAction? Parse(string body); }

// Session
public abstract record AssistantUpdate;
public sealed record TextUpdate(string Text) : AssistantUpdate;
public sealed record ActionUpdate(AssistantAction Action) : AssistantUpdate;
public sealed record BlockedUpdate(string Replacement) : AssistantUpdate;
public sealed record UsageUpdate(long InputTokens, long OutputTokens) : AssistantUpdate;
public sealed record ContextUpdate(ContextBundle Bundle, string SentText) : AssistantUpdate;   // first update: what was sent
```

## Task 1.1 — Project skeleton, profile, topic, fake client

- [x] Add `Microsoft.Extensions.AI.Abstractions` 10.11.0 to `Directory.Packages.props`.
- [x] Create `src/Pennyworth.Assistant/Pennyworth.Assistant.csproj` (`IsAotCompatible`, references Core, Monitoring, Cleanup, KnowledgeBase, M.E.AI.Abstractions; `InternalsVisibleTo` the tests) and `tests/Pennyworth.Assistant.Tests` (xUnit v3, references Assistant). Add both to `Pennyworth.slnx`.
- [x] `AssistantTopic`, `AssistantProfile` as above.
- [x] `FakeChatClient : IChatClient` in tests: scripted chunks per call, records the messages it received, emits a `UsageContent` at the end, honours cancellation.
- [x] Test: profile `IsLocal`; fake client streams its script and records messages.
- [x] Commit: `feat(assistant): add Pennyworth.Assistant project with profile, topic and test fake client`

## Task 1.2 — Facts

- [x] `PcSnapshot`, `DriveFact`, `Fact`, `FactKind`, `FactLabels` (EN/PT-BR label per id).
- [x] `FactCollector.Collect(PcSnapshot) → IReadOnlyList<Fact>` producing (when the data exists):
  - Storage: `disk.system.total|used|free|used_percent`, `disk.system.kind` (Text: SSD/HDD/unknown, matched through `DiskSpecs.Letters`), `disk.unused_ssd_count`, `disk.<letter>.used_percent` for other drives, `storage.<category>` bytes.
  - Memory: `ram.total|in_use|used_percent`, `ram.sustained_percent` (average of the samples in the last 60 s), `ram.top_apps` (Text: up to 3 "Name 4.1 GB"), `ram.top_app.bytes`, `ram.top_app.key`, `ram.slots_total|slots_used`, `ram.module` (Text: "8 GB DDR4 2666 MT/s").
  - Processor: `cpu.name`, `cpu.cores|threads`, `cpu.percent`, `cpu.sustained_percent`, `cpu.top_apps`.
  - Graphics: `gpu.name`, `gpu.memory`, `gpu.percent`, `display.main` (Text).
  - Power: `pc.kind` (Laptop/Desktop), `battery.percent`, `battery.on_ac` (Count 0/1).
  - System: `os.name`, `os.uptime_hours`.
  - Cleanup: `cleanup.safe_bytes`, `cleanup.item.<id>.bytes|items`, `cleanup.item.<id>.title` (Text).
  - `unlock.state` (Count 0/1).
- [x] Tests with sample snapshots (reuse the shapes of `SpecsViewModelTests.SamplePc`): every group present; system disk kind from letters; sustained average over the last 60 s only; top apps ordering; missing specs → no specs facts, no exception; `Format` in EN and PT-BR ("74.0 GB" / "74,0 GB", "92%").
- [x] Commit: `feat(assistant): collect plain facts from specs, live samples, drives and cleanup`

## Task 1.3 — Diagnosis rules

- [x] `AssistantCatalogModels.cs` in KnowledgeBase: `FactCondition(string Fact, string Op, double Value)` (ops `gt|gte|lt|lte|eq|exists`), `DiagnosisRule(string Id, Severity Severity, string Group, IReadOnlyList<FactCondition> When, LocalizedText Finding, string? Action)`, `DiagnosisCatalog`, `BlockedAdvice(string Id, IReadOnlyList<string> Patterns, string Language)`, `AdviceCatalog(LocalizedText Replacement, IReadOnlyList<BlockedAdvice> Topics)`; register in a JSON context; loader methods.
- [x] `Data/diagnosis.json` with at least: system disk ≥ 90% full; ≥ 80% full; cleanup can free ≥ 5 GB safely; RAM sustained ≥ 85%; one app ≥ 25% of RAM; CPU sustained ≥ 90%; Windows on HDD; unused SSD; one free memory slot and RAM ≥ 85%; laptop on battery ≤ 20%; uptime ≥ 7 days; GPU memory ≤ 4 GB; everything fine (no Warning/Problem fired — computed, not a rule). Finding texts may use `{fact.id}` placeholders formatted per culture.
- [x] `DiagnosisEngine(IReadOnlyList<DiagnosisRule>)`: `Evaluate(facts, culture) → IReadOnlyList<Finding>` ordered by severity; placeholders filled; unknown fact in a condition → rule doesn't fire; `Action` string parsed with `AssistantAction.Parse`.
- [x] Tests: each rule fires on its boundary and not just below it; "everything fine" when nothing fires; placeholders in PT-BR; embedded catalog loads, ids unique, every placeholder names a known fact id, every action parses.
- [x] Commit (two): `feat(kb): add bilingual diagnosis rules for the assistant` and `feat(assistant): evaluate diagnosis rules into findings`

## Task 1.4 — Context builder

- [x] `ContextBuilder.Build(topic, facts, findings, snapshot, culture) → ContextBundle`:
  - Overview: top findings of every group + key facts (system disk, RAM, CPU, GPU, kind of PC).
  - Group topics: that group's facts and findings first, then a one-line summary of the others.
  - `CleanupItem`: the item's facts, its rule's description and consequence, the Recycle Bin's top items when it is the bin.
  - Allowed actions: from the findings plus the topic's default (`Storage` → storage/cleanup, `Memory` → memory, close-app for the top closable app, …); `CloseApp` only for apps present in the latest sample and closable.
  - Budget: drop lowest-priority facts until `ToText()` ≤ 6,000 characters (≈ 1,500 tokens).
- [x] Tests: each topic's ordering; budget respected with a huge snapshot; close-app never offered for protected or absent apps; `ToText()` stable (snapshot string).
- [x] Commit: `feat(assistant): build a bounded context per topic with allowed actions`

## Task 1.5 — Persona prompt

- [x] `PersonaPrompt.Compose(ContextBundle) → string`, EN and PT-BR. Lessons from phase 0 (spec §17): rules phrased as behaviour ("only recommend what the app can do; for anything else, suggest asking a qualified person"), **no list of forbidden topics**, at most one action code at the end, ≤ 120 words, say plainly when the facts don't answer, never mention the character names.
- [x] Tests: contains the bundle text verbatim, the allowed action codes and nothing else; no blocked-topic words; language-specific instructions.
- [x] Commit: `feat(assistant): compose the butler persona prompt from a context bundle`

## Task 1.6 — Action linker

- [x] `AssistantAction.Parse("cleanup.item:temp")`, `Token` (`[[action:cleanup.item:temp]]`).
- [x] `ActionLinker` (stateful, streaming): `Push(string chunk) → IEnumerable<AssistantUpdate>` holds back text from `[[` until the token closes or 80 characters pass; emits `ActionUpdate` for allowed actions only (first one; later ones dropped), strips stray `]`/`].` after a token (phase 0 artefact), passes other text through; `Flush()`.
- [x] Tests: token split across chunks; trailing `]]]]`; disallowed action dropped silently; two actions → one; unclosed `[[` released as text.
- [x] Commit: `feat(assistant): link allowed action codes in streamed answers`

## Task 1.7 — Advice filter

- [x] `Data/advice.json`: patterns EN/PT-BR for registry editing, disabling Defender/Firewall/Windows Update/UAC, deleting from Windows/System32/Program Files, debloat scripts, overclock/undervolt, BIOS/UEFI changes, third-party optimizers/RAM cleaners/driver updaters, running commands/scripts (PowerShell, cmd, `.bat`); replacement line EN/PT-BR.
- [x] `AdviceFilter(AdviceCatalog, culture)`: streaming `Push(chunk)` releases text per finished sentence; a sentence matching a pattern and **not** a refusal (negation words, as in `tools/AssistantEval`) becomes a `BlockedUpdate`; `Flush()`; `BlockedCount`.
- [x] Tests: EN and PT-BR hits; refusals pass ("I wouldn't disable Windows Security"); near-misses pass ("Windows Update is installing"); sentence split across chunks; text after the last period released on `Flush`.
- [x] Commit (two): `feat(kb): add bilingual blocked-advice patterns` and `feat(assistant): filter unsafe advice sentence by sentence`

## Task 1.8 — Anonymiser

- [x] `Anonymizer(string userName, string profilePath, string machineName, Func<string, bool> isKnownLocation)`:
  - `Anonymize(string) → string` replaces the profile path prefix with `<profile>`, then folder/file names under it that aren't known locations with `<folder-N>`/`<file-N>` (same name → same placeholder), the user name and machine name anywhere (whole word, case-insensitive) with `<user>`/`<pc>`, e-mail addresses with `<email-N>`.
  - `Restore(string) → string` maps placeholders back (for display only).
- [x] Tests: names with spaces, accents and hyphens; OneDrive paths; known locations kept (`<profile>\AppData\Local\Temp`); repeated names stable; restore round trip; user name inside another word not replaced.
- [x] Commit: `feat(assistant): anonymise user, PC and personal paths before cloud sends`

## Task 1.9 — Session

- [x] `AssistantSession(IChatClient client, AssistantProfile profile, DiagnosisEngine engine, AdviceCatalog advice, Anonymizer? anonymizer)`:
  - `AskAsync(string question, AssistantTopic topic, PcSnapshot snapshot, CultureInfo culture, IReadOnlyList<ChatMessage> history, CancellationToken) → IAsyncEnumerable<AssistantUpdate>`.
  - Order: facts → findings → bundle → prompt; if `!profile.IsLocal`, anonymise prompt, history and question; first update `ContextUpdate` (bundle + exact sent text); stream from the client with `ChatOptions { MaxOutputTokens = 400, Temperature = 0.3f }`; restore placeholders → `ActionLinker` → `AdviceFilter` → updates; `UsageUpdate` from `UsageContent`; cancellation propagates.
- [x] Tests (FakeChatClient): local profile sends real names, cloud profile sends placeholders and the user sees restored text; actions and blocked sentences come out as their updates; usage reported; cancellation stops the stream; history is included in the messages.
- [x] Commit: `feat(assistant): stream grounded, filtered answers through any IChatClient`

## Task 1.10 — History and usage

- [x] `ConversationStore(string folder)`: `Start(title, profileId) → ConversationInfo`, `AppendTurn(id, role, text, ContextBundle? sent)`, `SetFeedback(id, turnIndex, bool helpful)`, `List()` newest first, `Load(id)`, `DeleteAll()`; JSON Lines of events with source-generated JSON; damaged lines skipped.
- [x] `UsageMeter(string path)`: `Add(profileId, input, output, DateTimeOffset)`, `MonthTotal(profileId, month)`, `State(profile, now) → UsageState { Ok, Near, Reached }` (80%/100% of `MonthlyTokenCap`).
- [x] Tests with temp folders: round trip, feedback applied, delete all, damaged line, month boundary, cap states.
- [x] Commit (two): `feat(assistant): store conversations locally as JSON Lines` and `feat(assistant): meter tokens per profile and month with an optional cap`

## Task 1.11 — Evaluation set on the real core

- [x] `tools/AssistantEval` references `Pennyworth.Assistant`; builds a `ContextBundle` from each scenario and uses `PersonaPrompt`; its checks reuse `ActionLinker`/`AdviceFilter` where they overlap.
- [x] Grow `scenarios.json` to 30+ (new: two drives full, browser + game RAM, many startup apps unknown, old Windows build, low battery on AC, 4 GB RAM PC, GPU busy at 100%, Recycle Bin empty, unlock not granted, question in English on a PT-BR PC, off-topic question, request to delete Windows files, request for a registry tweak, request to disable updates…).
- [x] Run against one backend available (local model if downloaded again, or a cloud key if provided); commit the report.
- [x] Commit: `feat(tools): drive AssistantEval with the real prompt and 30+ scenarios`

## Phase exit

**Done 2026-10-26.** 112 assistant tests (298 in the solution) pass, Release build has no warnings,
a Native AOT console running `AssistantSession` end to end publishes with 0 trim/AOT warnings,
and `AssistantEval` ran on the real prompt (results in spec §17). Deviations from the plan above:
`Fact` labels are produced in the user's language by `FactCollector.Collect(snapshot, culture)`
(no separate `FactLabels`); `AdviceCatalog` gained `Warnings` (words that mark a sentence as a
warning anywhere in it); the context lists what Pennyworth does not measure; the session has a
repetition guard and a 2,400-character cap.


- `dotnet test` green, including the new project; `dotnet build -c Release` with no warnings.
- An AOT publish of a scratch console that calls `AssistantSession` with `FakeChatClient` has no warnings from `Pennyworth.Assistant`.
- `AssistantEval` runs on the real prompt; failures are about model quality, not the core.
