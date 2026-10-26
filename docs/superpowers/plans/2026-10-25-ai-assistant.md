# Pennyworth AI Assistant — Phased Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each phase gets a detailed, code-level plan written when the previous phase is done; this document fixes the order, scope and exit criteria.

**Goal:** An optional assistant, in the voice of a discreet British butler, that diagnoses the user's own PC from data Pennyworth already measures, links only to existing app actions, and runs either on a cloud provider with the user's key or on a local model downloaded on demand.

**Architecture:** New `Pennyworth.Assistant` library (facts, diagnosis rules, context, anonymiser, prompts, advice filter, action links, history, usage) that depends only on `Microsoft.Extensions.AI` abstractions; `Pennyworth.Assistant.Providers` builds `IChatClient`s for OpenAI-compatible, Anthropic, Gemini and Azure OpenAI and stores keys in Windows Credential Manager; `Pennyworth.Assistant.Local` downloads, verifies and runs a small ONNX model with ONNX Runtime GenAI + DirectML. The app adds screen 07 "Ask" and contextual "Ask about this" buttons.

**Spec:** `docs/superpowers/specs/2026-10-25-ai-assistant-design.md`

**Release:** one user-facing release. Phases are internal: each is merged to `main` only when its exit criteria pass, and the Ask screen stays hidden behind a build flag (`PENNYWORTH_ASSISTANT`) until phase 6.

## Global Constraints

Everything in the M1 plan's Global Constraints still applies (net10.0-windows, central package versions, `TreatWarningsAsErrors`, `IsAotCompatible` on `src/` libraries, source-generated JSON, compiled bindings with `x:DataType`, all text in `Strings.resx` / `Strings.pt-BR.resx`, design tokens only). In addition:

- `Pennyworth.Assistant` never references a provider SDK, `HttpClient` or the ONNX runtime.
- No network call happens unless the user has configured a cloud profile and pressed Send (or started a model download).
- API keys never touch `settings.json`, logs, history files or exception messages.
- Commits: small steps, message under 150 characters, author AnGabSS only, no co-author line.
- Before adding any package, check its current docs (Context7) and pin the version in `Directory.Packages.props`.

## Phase overview

| Phase | Delivers | Depends on |
|---|---|---|
| 0. Spikes | AOT/trim verdict per package, evaluation harness skeleton, local model shortlist with PT-BR scores | — |
| 1. Core | `Pennyworth.Assistant`: facts, diagnosis rules, context builder, persona prompts, action linker, advice filter, anonymiser | 0 |
| 2. Cloud | `Pennyworth.Assistant.Providers`: 4 backends, Credential Manager keys, test connection, usage meter | 1 |
| 3. Ask screen | Screen 07, setup flow, send preview, streaming, history, feedback, settings | 1, 2 |
| 4. Local | `Pennyworth.Assistant.Local`: hardware check, verified download, ONNX GenAI client, idle unload | 0, 1, 3 |
| 5. Contextual | "Ask about this" on Home, Memory, Cleanup, Specs, 3D panels | 3 |
| 6. Release | Full evaluation on every backend, AOT publish, flag removed, docs | 1–5 |

Phases 2 and 4 touch different projects and can run in parallel once phase 1 is merged.

---

## Phase 0 — Spikes (decide before building)

Throwaway code lives in `tools/spikes/`; only the evaluation harness is kept.

- [x] **0.1 AOT check for each package.** In a scratch console app with `PublishAot=true` and `TreatWarningsAsErrors`, call each client once: `Microsoft.Extensions.AI` + `Microsoft.Extensions.AI.OpenAI` (custom endpoint), the official `Anthropic` SDK (`AsIChatClient`), the Gemini .NET SDK, Azure OpenAI client, `Microsoft.ML.OnnxRuntimeGenAI.DirectML`. Record trim/AOT warnings and whether the published exe works.
  - Exit: a table in `docs/superpowers/specs/2026-10-25-ai-assistant-design.md` §9 noting, per backend, "SDK" or "raw HTTP fallback".
- [x] **0.2 ONNX GenAI `IChatClient`.** Confirm the package version that ships an `IChatClient` implementation, its streaming and cancellation behaviour, DirectML device selection and CPU fallback, and peak RAM for a ~4B int4 model.
- [x] **0.3 Evaluation harness skeleton.** `tools/AssistantEval`: loads scenario JSON (facts + questions in EN/PT-BR), sends them through any `IChatClient`, runs the automatic checks (numbers only from facts, allowed actions only, blocked advice, length, language, no character names) and writes a Markdown report. Start with 10 scenarios; grow to 30+ in phase 1.
- [x] **0.4 Local model shortlist.** Run the harness on Phi-mini (ONNX, int4) and one or two other small instruction models published in ONNX GenAI format with permissive licences, CPU and DirectML. Record score, RAM, tokens/s, download size.
  - Exit: default model, its Hugging Face repo, file list and SHA-256 hashes written into the spec §10. If none passes PT-BR, write down the fallback chosen (English local answers or cloud-only PT-BR).

**Phase exit:** every open risk in spec §16 marked resolved or with a chosen fallback.

**Done 2026-10-25** — results in spec §17: OpenAI SDK + ONNX GenAI are AOT-clean; the Anthropic SDK is not (own client); Gemini and Azure go through the OpenAI SDK; Phi-3.5-mini is the provisional local default with conditions for phase 4.

---

## Phase 1 — Core (`Pennyworth.Assistant`)

New projects `src/Pennyworth.Assistant` and `tests/Pennyworth.Assistant.Tests`, added to `Pennyworth.slnx`.

- [x] **1.1 Project and abstractions.** Reference `Microsoft.Extensions.AI.Abstractions`; add `AssistantProfile`, `AssistantTopic` (overview, storage, memory, cleanup item, spec section, PC part), and a `FakeChatClient` in tests.
- [x] **1.2 Facts.** `Fact(string Id, string Label, string Value, string? Unit)` and `FactCollector` reading `PcSpecs`, `MonitorSample`, drive summaries, scan categories, cleanup estimates and unlock state. Tests with the existing sample data (`SamplePc`, `SampleTrees`).
- [x] **1.3 Diagnosis rules.** Bilingual `diagnosis.json` in `Pennyworth.KnowledgeBase` (id, condition over facts, severity, finding text EN/PT-BR, allowed action), loader with source-generated JSON, evaluator. One test per rule, including "everything fine".
- [x] **1.4 Context builder.** Picks facts and findings per topic, orders by relevance, enforces the token budget (character-based estimate, ≤ 1,500 tokens), produces a `ContextBundle` that is also what the send preview shows.
- [x] **1.5 Persona prompts.** System prompts in EN/PT-BR as resources: persona, "answer only from facts", allowed action tokens, length budget, "say you can't tell". Snapshot tests on the composed prompt.
- [x] **1.6 Action linker.** Parses `[[action:...]]`, validates against current data, returns text segments + `AssistantAction` records; invalid tokens dropped. Tests for every action and every invalid case.
- [x] **1.7 Advice filter.** Bilingual blocked patterns in the knowledge base; sentence-level check that works on streamed text; replacement line; flag on the turn. Tests in both languages, including near-misses that must pass ("Windows Update is installing, so the disk is busy").
- [x] **1.8 Anonymiser.** Replaces user name, profile path, machine name, e-mails and non-knowledge-base folder names under the profile with stable placeholders, and maps them back for display. Tests with realistic paths (names with spaces, accents, hyphens, OneDrive folders).
- [x] **1.9 Conversation service.** `AssistantSession.AskAsync(question, topic)` → builds context, anonymises (cloud only), streams from `IChatClient`, filters, links actions, de-anonymises, returns updates; cancellation. Tested end to end with `FakeChatClient`.
- [x] **1.10 History and usage.** JSON Lines `ConversationStore` (append, list, load, delete all) and `UsageMeter` (per profile, per month, cap) using `UsageContent`. Tests with a temp folder.
- [x] **1.11 Grow the evaluation set** to 30+ scenarios using the real diagnosis rules.

**Done 2026-10-26** — detailed plan and deviations in `2026-10-25-ai-assistant-phase1-core.md`, evaluation in spec §17.

**Phase exit:** all unit tests pass; `AssistantEval` runs against `FakeChatClient` and one real backend from phase 0 with no automatic-check failures caused by the core (prompt/context), only by model quality.

---

## Phase 2 — Cloud providers (`Pennyworth.Assistant.Providers`)

- [ ] **2.1 Credential store.** `ICredentialStore` + `WindowsCredentialStore` (`CredWriteW`/`CredReadW`/`CredDeleteW` via `LibraryImport`, target `Pennyworth/Assistant/<profile-id>`). Tests against the real store with a test-only target prefix, cleaned up after.
- [ ] **2.2 Profiles in settings.** Extend `AppSettings` with assistant profiles (no keys), default profile, monthly cap, "show what I send" per profile; migration from settings without them.
- [ ] **2.3 Chat client factory.** OpenAI SDK for OpenAI-compatible (endpoint + model), Gemini (its OpenAI-compatible endpoint) and Azure OpenAI (resource v1 endpoint + deployment); a small own Anthropic Messages client (`HttpClient`, source-generated JSON, SSE streaming, usage) because the official SDK fails under AOT (spec §17). Shared pipeline: usage metering, timeout, one retry on transient errors, cancellation.
- [ ] **2.4 Test connection.** One short prompt; returns success, latency, model name or a classified error (bad key, unknown model, network, quota).
- [ ] **2.5 Error mapping.** Provider errors → `AssistantError` kinds used by the UI; nothing sensitive in messages.
- [ ] **2.6 Evaluation per backend.** Run `AssistantEval` on at least one model per backend; record results.

**Phase exit:** each backend answers a scenario through `AssistantSession`; keys survive app restarts and never appear in files under `%LOCALAPPDATA%\Pennyworth`.

---

## Phase 3 — Ask screen (app)

- [ ] **3.1 Strings.** EN/PT-BR strings for the screen, setup, send preview, errors, settings (through the strings generator; placeholder rules of `StringsTests` still apply).
- [ ] **3.2 Sidebar 07 and page.** `Page.Ask`, `GoAskCommand`, `AskViewModel` registered in `AppServices` (optional, like `Specs`), sidebar entry hidden behind `PENNYWORTH_ASSISTANT`.
- [ ] **3.3 Setup flow.** "Run on this PC" (placeholder until phase 4) / "Use my own AI account": backend, endpoint, model, key, test connection, save profile.
- [ ] **3.4 Chat view.** Messages, streaming with Stop, starter questions from top findings, backend badge, tokens per answer, permanent "AI can be wrong" note, action buttons from `ActionLinker` wired to existing commands (cleanup preview, close-app dialog, specs…).
- [ ] **3.5 Send preview.** Panel showing the anonymised `ContextBundle` and question; Send / Edit / Cancel; "don't show again for this provider".
- [ ] **3.6 History and feedback.** Conversation list, open, delete all (with confirmation); Helpful / Not helpful per answer.
- [ ] **3.7 Settings.** Profiles list, default, monthly cap with 80%/100% notes, delete history.
- [ ] **3.8 Verify.** View-model tests; headless renders in dark/light, EN/PT-BR, 1100 px and 720 px; a real-window check of streaming and Stop.

**Phase exit:** with a cloud profile, a beginner can set up, ask, see what is sent, follow an action button to the right screen, and delete history.

---

## Phase 4 — Local model (`Pennyworth.Assistant.Local`)

- [ ] **4.0 Runtime package and variant.** Choose `OnnxRuntimeGenAI.WinML` 0.18+ or `DirectML` 0.14.1 (frozen since June 2026); get Phi-3.5-mini running on the GPU (CPU variant on DirectML or WinML); measure the CPU-optimised variant; add repetition penalty and a loop guard; re-run `AssistantEval` with the phase 1 prompt and read every PT-BR answer. Pin the default model and hashes in spec §10.

- [ ] **4.1 Hardware check.** RAM total/available, free space on the models drive, DirectML adapters and VRAM (reuse `WindowsSpecsReader` data); verdict + reasons. Tests on synthetic specs.
- [ ] **4.2 Model catalogue.** Embedded entry for the default model chosen in phase 0: repo, revision, files, sizes, SHA-256, licence, RAM estimate.
- [ ] **4.3 Download.** Resumable HTTP range download per file into a `.partial` file, SHA-256 verification, atomic move, progress (bytes, speed, ETA), cancel, remove model. Tests with a local test server and a tampered file.
- [ ] **4.4 Local chat client.** ONNX Runtime GenAI `IChatClient` with DirectML and CPU fallback; lazy load on first question; unload after 5 minutes idle or when leaving Ask; cancellation.
- [ ] **4.5 Setup flow.** Replace the phase 3 placeholder: hardware verdict → download → first answer; "Runs on this PC" badge; no send preview for local.
- [ ] **4.6 Evaluation.** Run the full set on the local model in EN and PT-BR on a CPU-only machine and a DirectML machine.

**Phase exit:** on a PC without any key, a user can download, verify and use the local model; app memory returns to its normal level after the idle unload.

---

## Phase 5 — Contextual "Ask about this"

- [ ] **5.1 Topic entry points.** `AskViewModel.AskAbout(AssistantTopic, string suggestedQuestion)`.
- [ ] **5.2 Buttons.** Home summary, each Memory app row, each cleanup item and its preview, each Specs section, 3D part panels; hidden when no profile is configured (shows "Set up the assistant" instead on the Ask screen).
- [ ] **5.3 Tests and renders.** Each button opens Ask with the right topic and facts; renders of each screen with the button.

**Phase exit:** from any of those screens, one click reaches a grounded answer about that item.

---

## Phase 6 — Release

- [ ] **6.1 Full evaluation** on the default local model and one model per cloud backend; report committed under `tools/AssistantEval/reports`.
- [ ] **6.2 AOT publish** of the app with the assistant; startup time and idle memory unchanged when the assistant is not configured (compare with the previous release).
- [ ] **6.3 Privacy review** of a real session: everything leaving the PC matches the send preview; no keys or personal names in logs or history files.
- [ ] **6.4 Remove the `PENNYWORTH_ASSISTANT` flag**, update the README and the v1 spec's non-goals, release notes in EN/PT-BR.

**Release exit:** all phase exits still hold on the release build.
