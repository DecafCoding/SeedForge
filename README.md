# SeedForge

A single-user .NET 10 Blazor Web App that turns YouTube videos into scored story **concepts**. Channels are polled for new uploads, transcripts are extracted, sliced into segments, mined for **ideas**, scored on multiple axes, and the survivors are built into story concepts.

**Pipeline:** `Channel → Video → Transcript → Segment → Idea → IdeaScore → Concept`

The governing data rule: **sources are immutable** (`Transcript`, `Segment`, `Idea` never change once written) and **derivatives are append-only** (`IdeaScore`, `Concept` accumulate, with one `Concept` marked active per idea). Re-running a stage never destroys what it replaces.

> See `docs/prd.html` for the authoritative product spec and `docs/phases/` for per-phase implementation plans.

## Status

Phase 0 (Foundation & Data Spine) is complete: the SQLite data model, the single `ApplicationDbContext`, startup auto-migration, the vertical-slice folder structure, and a build-enforced architecture test are in place.

Phase 1 (LLM Client & Call Logging) is complete: a self-contained `LlmClient` (`Services/Ai/`) that talks to any OpenAI-compatible `/chat/completions` endpoint with free-text and strict `json_schema` structured calls, per-slot configuration (`Ai:Slots`), an `AiCallLogger` decorator that records one `AiCallLog` per call (tokens, latency, cost, correlation id), and a `/diagnostics` page to test each slot's connection.

Phase 2 (The Pipeline, on a Pasted Transcript) is complete: the four pipeline stages are implemented as vertical slices in `Features/` — **SegmentTranscript** (Seed), **ExtractIdeas** (Extraction), **ScoreIdeas** (Scoring, one pass + threshold), and **BuildConcept** (Concept) — composed by a `PipelineRunner` orchestrator (`Pipeline/`) that threads one correlation id across the run. A **`/pipeline`** page pastes a transcript, runs it end to end, and shows the scored ideas, developed concepts, and the full AI trace. Segmentation uses structured boundary anchors located verbatim in the source so segments stay faithful slices of the original transcript.

Phase 3 (Versioning, Regeneration & the Compare Loop) is complete: switchable, named **config profiles** (`ConfigProfile`) bundle the per-slot settings, with exactly one active — the `LlmOptionsResolver` is now DB-backed and resolves each slot from the active (or a chosen) profile, falling back to `appsettings` and filling a blank OpenAI key from user-secrets so secrets never live in the DB. Three rebuild operations reuse the existing slices without re-running upstream: **RegenerateConcept** (run-now rebuild of one concept with an optional profile override), **RescoreIdea** (appends a new score and applies the stale-not-deleted cascade), and **ReplayCall** (reissues a stored call's messages against a different config for a side-by-side A/B). A **`/config`** page switches the active profile and tests each slot, and a **`/concepts`** page browses an idea's score/concept version history with active/stale flags and the regenerate / rescore / replay actions.

## Tech Stack

- **.NET 10** / ASP.NET Core Blazor Web App (Interactive Server)
- **EF Core 10** with the **SQLite** provider
- **ASP.NET Core Identity** (Individual Accounts; present but not gating in this phase)
- **xUnit** + **NetArchTest.Rules** for build-enforced architecture boundaries

## Project Layout

```
SeedForge/                     # project + git root (Solution: SeedForge.slnx)
├─ Components/                 # Blazor components + Identity account pages
├─ Data/                       # ApplicationDbContext, ApplicationUser, Migrations
├─ Domain/                     # POCO entities + enums (no EF dependency)
├─ Features/                   # vertical slices (Segmentation, Extraction, Scoring, Concepts, Config, Observability)
├─ Pipeline/                    # PipelineRunner orchestrator (driving adapter)
├─ Services/                   # shared services (Ai/)
├─ Workers/                    # background workers (added in later phases)
└─ tests/
   ├─ SeedForge.ArchitectureTests/   # NetArchTest boundary rules
   └─ SeedForge.UnitTests/           # xUnit unit tests (AI plumbing, stubbed HTTP)
```

Folders map to namespaces (`SeedForge.Domain`, `SeedForge.Data`, ...). Architecture is **vertical-slice**: no repository/UoW wrapper — handlers use the `DbContext` directly. A single `DbContext` (Identity + domain) keeps one migration stream.

## Build, Test, Run

Run all commands from the project root (`SeedForge/`):

```bash
dotnet build SeedForge.slnx
dotnet test  SeedForge.slnx
dotnet run   --launch-profile http        # http://localhost:5222
dotnet format SeedForge.slnx              # formatting
```

The HTTPS profile also serves `https://localhost:7211`.

## Database

Persistence is **SQLite**. The connection string in `appsettings.json` is:

```json
"ConnectionStrings": { "DefaultConnection": "Data Source=seedforge.db" }
```

The schema is created/updated **automatically at startup** via `Database.Migrate()` — no manual migration step is needed to run the app. The `seedforge.db` file is created relative to the content root and is git-ignored.

### EF Core Migrations

There is exactly one `DbContext`, so no `--context` flag is required:

```bash
dotnet ef migrations add <Name> --output-dir Data/Migrations
dotnet ef database update
dotnet ef migrations has-pending-model-changes   # exits 0 when snapshot matches the model
```

## Architecture Tests

`tests/SeedForge.ArchitectureTests` fails the build if dependency rules are violated:

- `SeedForge.Services` must not depend on `SeedForge.Features`
- `SeedForge.Domain` must not depend on `SeedForge.Services`, `SeedForge.Features`, `SeedForge.Data`, or `Microsoft.EntityFrameworkCore`

These run as part of `dotnet test SeedForge.slnx`.

## Configuration & Secrets

Non-secret defaults ship in `appsettings.json`. Secrets are never committed — supply them via user-secrets or environment variables:

```bash
dotnet user-secrets set "<Section>:<Key>" "<value>"
```

## AI Configuration

The pipeline's model calls are configured per **slot** under the `Ai:Slots` section of `appsettings.json`. There are five slots — `Seed`, `Extraction`, `Scoring`, `Concept`, `Conversation` — each binding to a connection (`BaseUrl`, `ApiKey`, `Model`, `TimeoutSeconds`, optional `Temperature`/`ReasoningEffort`). All slots default to the reference local rig (`http://192.168.50.102:8070`).

A **blank** `BaseUrl` routes to hosted OpenAI (`https://api.openai.com/v1/`). Hosted API keys are **never committed** — override them via user-secrets, e.g.:

```bash
dotnet user-secrets set "Ai:Slots:Concept:ApiKey" "sk-..."
```

Every model call is recorded as one `AiCallLog` row (stage, slot, tokens, latency, estimated cost, correlation id). Use the **`/diagnostics`** page to test connectivity to each slot's endpoint, and the **`/pipeline`** page to run a pasted transcript through the full engine.

### Config Profiles

A `ConfigProfile` is a named, switchable bundle of all five slot settings; exactly one is active at a time. On first run three profiles are seeded (idempotently) — **"All Local"** (active, mirroring the `appsettings` rig), **"Local + OpenAI Concept"**, and **"All OpenAI"**. The resolver reads the active profile (falling back to `appsettings` when none exists). Switch the active profile and test each slot on the **`/config`** page.

OpenAI keys are **never stored in a profile row**. When a profile's OpenAI slot has a blank key, the resolver fills it from user-secrets:

```bash
dotnet user-secrets set "Ai:OpenAiApiKey" "sk-..."
```

### Versioning & the Compare Loop

The **`/concepts`** page surfaces an idea's append-only history and three run-now actions:

- **Regenerate concept** — rebuild one concept against an optional profile override (a new active version is appended; the prior is flipped inactive — no upstream re-run).
- **Re-score idea** — append a new `IdeaScore`; on a drop below threshold the idea's concepts are flagged stale (never deleted), cleared again on a pass.
- **Replay** — reissue a stored call's messages against a chosen profile and compare the original and new outputs side by side (the original log is immutable; the replay writes a new `AiCallLog`).

### Pipeline Configuration

The scoring threshold is configured under the `Pipeline` section of `appsettings.json`:

```json
"Pipeline": { "ScoreThreshold": 0.6 }
```

An idea survives scoring when the mean of its four axes (Novelty, Coherence, SciFiPotential, FormulaFit) is at least `ScoreThreshold`. Only survivors are developed into concepts.
