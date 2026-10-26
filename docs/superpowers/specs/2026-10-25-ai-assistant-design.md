# Pennyworth AI Assistant — Design Spec

**Date:** 2026-10-25
**Status:** Decisions made in a Q&A session with the owner; pending spec review
**Amends:** `2026-10-24-pennyworth-v1-design.md` §1 non-goal "AI-generated explanations" and
§2 "Explanations: no AI, no network". The curated knowledge base stays the source of truth;
the assistant is optional and off until the user sets it up.

## 1. Purpose

Pennyworth already explains storage, memory, cleanup and specs in plain language, but it
cannot answer the questions a beginner actually asks about **their own PC**: "why does my PC
freeze when I open this game?", "is 16 GB of RAM enough for what I do?", "what would make
this faster?".

The assistant is a chat, written in the voice of a discreet British butler, that answers
those questions **from the real data the app has already measured**, suggests only actions
the app already offers, and never acts without the same preview and confirmation the rest of
the app uses.

### Success criteria

- Every answer about the user's PC is grounded in facts the app measured; the assistant
  never invents a number (checked by the evaluation set, §10).
- An action suggested by the assistant opens the existing screen (cleanup preview, close app
  dialog); the assistant itself never deletes, closes or changes anything.
- Before anything is sent to a cloud provider, the user can see exactly what will be sent,
  with personal names and paths anonymised.
- With no assistant configured, Pennyworth behaves exactly as today: no network calls, no
  extra memory, no download.
- The local model is never downloaded without the user asking, and the app warns (but does
  not block) when the PC is unlikely to run it well.
- English and Portuguese (Brazil), like the rest of the app.

### Non-goals

- Free-form system changes: registry edits, services, drivers, overclocking, BIOS settings.
- Tool calling / autonomous agents (small local models are unreliable at it; see §6).
- Long-term memory of facts about the user across conversations (only a deletable history).
- A Pennyworth-hosted cloud service; the user brings a key or runs locally.
- Voice input/output, images, screenshots.

## 2. Decisions

| Topic | Decision |
|---|---|
| Value | Diagnose the user's own PC from measured data, not generic hardware Q&A |
| Actions | Only existing app actions, always through their existing preview/confirm screens |
| Persona | Tone of a formal, dry-witted British butler; never names Alfred, Batman, Wayne or quotes the character (trademark) |
| Grounding | The app computes the facts and builds the context; the model only explains and phrases. No tool calling |
| Abstraction | `Microsoft.Extensions.AI` `IChatClient` for every backend |
| Cloud providers | OpenAI-compatible endpoint (OpenAI, OpenRouter, Groq, Ollama, LM Studio…), Anthropic, Google Gemini, Azure OpenAI |
| Local model | ONNX Runtime GenAI with DirectML; optional, downloaded on demand |
| Local model choice | Picked by the PT-BR evaluation set: Phi-mini vs 1–2 small ONNX alternatives |
| Download source | Official Hugging Face repository, SHA-256 pinned in the app, resumable, cancellable |
| Hardware check | Automatic RAM/disk/GPU check with a plain-language warning; never blocks |
| API keys | Windows Credential Manager; never in `settings.json` |
| Privacy | Preview of everything sent to the cloud; user names and personal paths anonymised |
| UI | New sidebar screen **07 Ask** plus contextual "Ask" buttons on other screens |
| History | Stored locally only, one button deletes all of it |
| Safety | Blocked-advice filter, evaluation set, helpful/not-helpful feedback, permanent "AI can be wrong" note |
| Cost | Tokens per answer and month total for paid providers, optional monthly cap |
| Delivery | One user-facing release, built in internal phases (see the plan) |

## 3. Architecture

```
            ┌──────────────── Pennyworth.App (Avalonia) ───────────────┐
            │ AskViewModel  ←  "Ask" buttons on Home/Memory/Cleanup/…   │
            └───────────────┬──────────────────────────────────────────┘
                            │ question + topic
            ┌───────────────▼─────────────── Pennyworth.Assistant ─────┐
            │ ContextBuilder  →  Facts (diagnosis rules over live data) │
            │ Anonymizer      →  SendPreview (what leaves the PC)       │
            │ PromptComposer  →  persona + facts + allowed actions      │
            │ AdviceFilter    →  blocks unsafe advice in the answer      │
            │ ActionLinker    →  [[action:...]] tokens → app commands   │
            │ ConversationStore, UsageMeter, Settings                    │
            └───────────────┬──────────────────────────────────────────┘
                            │ IChatClient (Microsoft.Extensions.AI)
     ┌──────────────┬───────┴──────┬──────────────┬──────────────┐
  OpenAI-compat.  Anthropic     Gemini       Azure OpenAI   Local ONNX GenAI
  (OpenAI SDK)   (official SDK)                              (DirectML, on demand)
```

### New projects

| Project | Responsibility | Depends on |
|---|---|---|
| `Pennyworth.Assistant` | Facts, diagnosis rules, context building, anonymisation, prompts, advice filter, action links, history, usage, settings model. **No network, no model runtime**: talks only to `IChatClient`. | Core, Monitoring, Cleanup, KnowledgeBase, `Microsoft.Extensions.AI.Abstractions` |
| `Pennyworth.Assistant.Providers` | Builds an `IChatClient` per backend; key storage (Credential Manager); model catalogue | Assistant, provider SDKs |
| `Pennyworth.Assistant.Local` | Model download/verification, hardware check, ONNX Runtime GenAI `IChatClient` | Assistant, `Microsoft.ML.OnnxRuntimeGenAI.DirectML` |
| `tests/Pennyworth.Assistant.Tests` | Unit tests with a fake `IChatClient` | Assistant |
| `tools/AssistantEval` | Runs the evaluation set against any configured backend; not shipped | Assistant, Providers, Local |

Keeping the runtime and SDKs out of `Pennyworth.Assistant` lets the core logic stay small,
AOT-safe and fully testable, and lets the app load the heavy local runtime only when used.

## 4. Grounding: facts first, words second

The model is never asked to work out anything about the PC. The app does that, then asks the
model to explain it.

1. **Facts.** `FactCollector` reads what the app already has: `PcSpecs`, the latest
   `MonitorSample` (RAM/CPU/GPU, top apps), drive summaries and the last scan's categories,
   cleanup estimates, unlock state. Each fact is a typed record with a stable id, a value and
   a plain-language label (e.g. `ram.used_percent = 82`, `disk.c.free_gb = 74`).
2. **Diagnosis rules.** `DiagnosisRules` (curated in the knowledge base, bilingual, like
   cleanup rules) turn facts into findings with a severity and an allowed action:
   "C: is 92% full" → `action:cleanup`; "Chrome uses 4.1 GB of 16 GB" → `action:memory`;
   "RAM over 85% for most of the last minute" → advice "close apps you aren't using" or, if
   persistent, "more RAM would help". Rules are deterministic and unit tested.
3. **Topic selection.** The question's entry point picks the relevant facts: an "Ask" button
   passes its topic (`memory`, `storage`, `cleanup.item:<rule>`, `spec:<section>`); the chat
   screen sends a compact overview (top findings + key specs). The context stays within a
   budget (target ≤ 1,500 tokens) so small local models keep it all in view.
4. **Answer.** The model receives persona + facts + findings + the list of actions it may
   link, and is told to answer only from those facts and to say "I can't tell from what I can
   see" otherwise.

## 5. Persona

System prompt (both languages) describes a formal, warm, slightly dry-witted British butler
who:

- addresses the user politely, explains in plain words, avoids jargon or defines it once;
- is honest about uncertainty and never pretends to have measured something;
- keeps answers short (target ≤ 120 words) and ends with at most one suggested action;
- never mentions Alfred, Batman, Bruce/Wayne, Gotham or quotes the character.

The persona never overrides safety: when a rule or filter applies, it is stated plainly.
In Portuguese the butler addresses the user as "você", polite but not stiff, matching the
app's existing texts.

## 6. Actions

The model may only link actions from a fixed list, written as tokens the app replaces with
buttons:

| Token | Opens |
|---|---|
| `[[action:cleanup]]` | Cleanup screen (measure + select) |
| `[[action:cleanup.item:<rule-id>]]` | That item's "What will be deleted" preview |
| `[[action:memory]]` | Memory & CPU screen |
| `[[action:close-app:<app-key>]]` | Existing "Close app?" dialog for that app |
| `[[action:storage]]` / `[[action:folders:<path>]]` | Storage summary / folder browser at a path |
| `[[action:specs]]` | Specs screen |
| `[[action:unlock]]` | Existing unlock dialog |

`ActionLinker` drops any token not in the list or not valid for the current data (unknown
rule id, app no longer running, path outside the scanned drive). Tool calling is not used:
the same mechanism works identically for cloud and local models.

## 7. Safety: blocked advice

`AdviceFilter` runs on every answer (streamed text is checked per sentence before display):

- Blocked topics (bilingual patterns in the knowledge base): registry editing, disabling
  Windows Defender/Firewall/Windows Update/UAC, deleting from `Windows`/`System32`/
  `Program Files`, "debloat" scripts, overclocking/undervolting, BIOS changes, third-party
  "optimizers" or "RAM cleaners", running downloaded scripts or commands.
- A blocked sentence is replaced by a fixed, persona-styled line ("That is not something I
  would recommend; it can leave Windows unstable.") and the turn is flagged in history.
- A permanent note under the chat: "Answers are generated by AI and can be wrong. Pennyworth
  never changes anything without asking you."

## 8. Privacy

- **Local model:** nothing leaves the PC; no preview needed (a small "Runs on this PC" badge).
- **Cloud:** before the first message of a conversation, and whenever the context changes
  topic, a **"What I'm about to send"** panel lists the facts and the question, exactly as
  sent. Options: Send / Edit question / Cancel, plus "Don't show again for this provider"
  (re-shown when the provider or model changes).
- **Anonymiser:** replaces the Windows user name, the user profile path, the machine name,
  e-mail addresses, and folder/file names under the user profile that are not in the
  knowledge base with stable placeholders (`<user>`, `<profile>\Documents\<folder-1>`).
  Placeholders are mapped back in the displayed answer. Known locations (e.g.
  `AppData\Local\Temp`) are kept because they carry meaning.
- No telemetry. Feedback and history stay local.

## 9. Backends

All backends are built by `ChatClientFactory` from an `AssistantProfile` (backend, model id,
endpoint, display name) and wrapped by a shared pipeline (usage metering, cancellation,
timeout, retry once on transient errors).

| Backend | Client | Notes |
|---|---|---|
| OpenAI-compatible | OpenAI .NET SDK `ChatClient` + `AsIChatClient()` with custom endpoint | Covers OpenAI, OpenRouter, Groq, Ollama, LM Studio, Gemini's OpenAI-compatible endpoint |
| Anthropic | Official `Anthropic` SDK, `IAnthropicClient.AsIChatClient(model)` | |
| Google Gemini | Native SDK if it is AOT-safe (phase 0), otherwise Gemini's OpenAI-compatible endpoint | |
| Azure OpenAI | Azure OpenAI client → `ChatClient` → `AsIChatClient()` | Endpoint + deployment name |
| Local | ONNX Runtime GenAI `IChatClient` (DirectML; CPU fallback) | Loaded lazily in-process; unloaded after idle |

**Keys** are stored in Windows Credential Manager (`CredWrite`/`CredRead` via `LibraryImport`,
target `Pennyworth/Assistant/<profile-id>`), never logged or shown again in full (last 4
characters only).

**Model lists** are typed by the user, with suggestions from a small catalogue; "Test
connection" sends a one-word prompt and reports success, latency and the model's echoed name.

## 10. Local model

- **Choice:** the default is decided in phase 0 by running the evaluation set in PT-BR and EN
  on Phi-mini and one or two other small instruction models published in ONNX GenAI format
  with permissive licences. Selection criteria, in order: no invented facts, PT-BR quality,
  RAM use, speed on CPU-only machines.
- **Hardware check:** before offering the download, measure free disk on the target drive,
  total and available RAM, DirectML-capable GPU and VRAM. Show a plain verdict ("Should run
  well" / "Will be slow on this PC" / "Not enough free space") with the reasons and a link to
  set up a cloud provider instead. The user can always continue.
- **Download:** from the official Hugging Face repository; file list and SHA-256 pinned in
  the app per model version; resumable; progress, speed and remaining time shown; files go to
  `%LOCALAPPDATA%\Pennyworth\models\<model>\<version>`; a model that fails verification is
  deleted and never loaded. "Remove model" frees the space and is listed in Settings with its
  size.
- **Runtime:** loaded on first question, kept while the Ask screen is open, unloaded after 5
  minutes idle so the app returns to its normal footprint. Generation is cancellable.
- **No network** is used by the local path after the download.

## 11. UI

- **Sidebar 07 "Ask"** ("Pergunte"): conversation list on the left of the content (or a
  dropdown at narrow widths), chat on the right, suggested starter questions built from the
  current top findings ("Why is C: almost full?"), the backend badge (local/cloud + model),
  token count per answer for paid backends, Stop button while streaming.
- **Contextual buttons:** a small "Ask about this" on the Home summary, each Memory app row,
  each cleanup item, each Specs section and the 3D part panels; it opens Ask with the topic
  pre-selected and a suggested question.
- **Action buttons** in answers come from `ActionLinker` and look like the app's existing
  small buttons.
- **Feedback:** "Helpful / Not helpful" per answer, stored with the answer in history.
- **Setup flow** (first open of Ask): choose "Run on this PC" (hardware check → download) or
  "Use my own AI account" (backend → key → model → test). Both reachable later in Settings.
- **Settings:** profiles, default profile, monthly token cap, "Show what I send" toggle per
  provider, delete history, remove local model.
- Same design system: tokens from `Theme/Tokens.axaml`, mono labels, `TextCase.Upper` labels,
  dark/light, EN/PT-BR via `Strings.resx`.

## 12. History and usage

- Conversations are stored as JSON Lines under `%LOCALAPPDATA%\Pennyworth\assistant\`, one
  file per conversation, with source-generated JSON (AOT). Facts sent are stored with each
  turn so an answer can be reviewed later against what the model saw.
- "Delete all conversations" removes the folder after confirmation.
- `UsageMeter` reads `UsageContent` from each response; the month total and optional cap are
  per profile. At 80% of the cap a note appears; at 100% cloud questions stop until the next
  month or until the cap is raised.

## 13. Evaluation

`tools/AssistantEval` replays scenarios against a chosen backend and writes a report:

- **Scenarios:** 30+ synthetic PCs as fact sets (full disk, RAM pressure, old HDD, low VRAM
  for a game, laptop on battery, everything fine…), each with questions in EN and PT-BR.
- **Checks per answer:** cites only provided numbers (automatic: every number in the answer
  must appear in the facts, with tolerance for rounding); links only allowed actions; no
  blocked advice; length budget; language matches the question; persona does not name the
  character.
- Run on every prompt or default-model change; results kept under `tools/AssistantEval/reports`.

## 14. Errors

- No profile configured → the Ask screen shows the setup flow, nothing else.
- Network/provider errors → plain message in the persona's voice, with the provider's error
  code in a "Details" expander; the question can be retried.
- Invalid or revoked key → "Your key was refused" with a link to the profile.
- Local model missing/corrupt → offer re-download; never crash the app.
- Every failure is logged to the local error log (v1 spec, M6) without keys or message contents.

## 15. Testing

- Unit (`Pennyworth.Assistant.Tests`, fake `IChatClient`): fact collection from sample
  specs/monitor data, every diagnosis rule, context budget, anonymiser round trip, action
  linker validation, advice filter (EN/PT-BR), usage cap, history store.
- App tests: Ask view model flows (setup, send preview, streaming, stop, feedback, action
  button opens the right screen), contextual buttons pass the right topic.
- Integration (manual / opt-in, needs keys or the model): `AssistantEval` per backend.
- AOT: publish with `PublishAot=true` in CI once phase 0 confirms the packages trim cleanly.

## 16. Open risks

| Risk | Mitigation |
|---|---|
| Provider SDKs or ONNX Runtime GenAI not AOT/trim safe | Phase 0 spike; fall back to raw `HttpClient` + source-generated JSON clients behind `IChatClient` |
| No small local model is good enough in PT-BR | Evaluation decides; fallback: local answers in English with a note, or cloud-only for PT-BR |
| Anonymiser misses a personal name in a path | Default to anonymising every non-knowledge-base segment under the profile; tests with real-looking paths |
| Local model memory pressure on the target audience's PCs | Optional download, hardware verdict, lazy load, idle unload |
| Persona drifts into inventing facts | Facts-first prompt, number check in evaluation, short answer budget |
| One large release | Internal phases, each merged only when its tests and evaluation pass |
