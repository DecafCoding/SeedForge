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

Phase 4 (Real Ingestion — Single Video) is complete: a YouTube URL becomes concepts end to end with no manual transcript. A typed **`ApifyClient`** (`Services/Apify/`) posts to the Apify `run-sync-get-dataset-items` endpoint for the `streamers~youtube-scraper` actor; **`ApifyIngestionService`** resolves the video id (via the shared `Services/YouTube/YouTubeUrl` helper), runs the actor, and parses the first dataset item defensively (subtitles / transcript / captions / segments) into transcript text + title + channel + the raw item + best-effort cost. The **IngestTranscript** slice (`Features/Ingestion/`) persists the immutable `Video` + `Transcript`, recording **Done / NoTranscript / Failed** distinctly (a captionless video is *not* a failure), idempotent on the YouTube id so an already-fetched video is never re-paid. `PipelineRunner` gains `RunFromTranscriptAsync` (the reusable four-stage core) and `RunFromUrlAsync` (ingest → run), and the **`/pipeline`** page accepts a YouTube URL alongside the paste-text path.

Phase 5 (Queues & Workers — Unattended Runs) is complete: SeedForge now runs without you. Two durable, DB-backed queues (`Services/Queues/`) sit over the existing rows — a `Video` row *is* the video job, a `ConceptJob` row is the concept job — each with atomic claim (Pending→InProgress in a transaction), exponential **backoff** to a terminal `Failed` after `MaxAttempts`, pending-count, and **process-now** (jump the line + wake the worker). Two **`BackgroundService`** workers (`Workers/`) drain them on independent cadences: the **ProcessingWorker** ingests → segments → extracts → scores and **stops at the scoring seam**, enqueuing one `ConceptJob` per survivor (`Video` set `Done`, or `ProcessedNoIdeas` when zero survive — never built inline), while the **ConceptWorker** builds exactly one active `Concept` per job on its own cadence (honoring an optional profile/slot override), so a slow/paid Concept model never stalls the cheap local processing loop. Each worker is a singleton that opens a DI scope per tick and delegates to an extracted, unit-testable `ProcessOnceAsync`; a job error reschedules with backoff and never crashes the host. A `WorkerControl` singleton provides per-worker **pause/resume** and a wake signal. A bare **`/queues`** page shows pending counts + drain-time ETA with pause/resume, process-now, and URL enqueue. **Both workers start paused on boot** to avoid surprise Apify/model spend.

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
├─ Features/                   # vertical slices (Segmentation, Extraction, Scoring, Concepts, Config, Observability, Ingestion)
├─ Pipeline/                    # PipelineRunner orchestrator (driving adapter)
├─ Services/                   # shared services (Ai/, Apify/, YouTube/, Queues/)
├─ Workers/                    # background workers (ProcessingWorker, ConceptWorker) + WorkerControl
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

### Ingestion (Apify)

Transcripts are fetched from YouTube via the Apify `streamers~youtube-scraper` actor. Non-secret defaults ship under the `Apify` section of `appsettings.json` (`BaseUrl`, `ActorId`, `TimeoutSeconds`); the **token is blank in source** and must be supplied via user-secrets:

```bash
dotnet user-secrets set "Apify:Token" "apify_api_..."
```

The token is sent as a query-string parameter. Paste a YouTube URL (or bare 11-char id) on the **`/pipeline`** page to fetch a transcript and run it through the engine. A real fetch **bills the Apify account** — every automated test uses a stubbed handler / fake service and never calls Apify.

### Pipeline Configuration

The scoring threshold is configured under the `Pipeline` section of `appsettings.json`:

```json
"Pipeline": { "ScoreThreshold": 0.6 }
```

An idea survives scoring when the mean of its four axes (Novelty, Coherence, SciFiPotential, FormulaFit) is at least `ScoreThreshold`. Only survivors are developed into concepts.

### Queues & Workers

Two background workers run inside the web app and drain durable, DB-backed queues so concepts accumulate unattended. They are configured under the `Workers` section of `appsettings.json`:

```json
"Workers": {
  "ProcessingIntervalSeconds": 1800,
  "ConceptIntervalSeconds": 60,
  "MaxAttempts": 5,
  "BackoffBaseSeconds": 30
}
```

- `ProcessingIntervalSeconds` / `ConceptIntervalSeconds` — how often each worker wakes (a process-now or enqueue wakes it early).
- `MaxAttempts` — failed jobs retry with exponential backoff (`BackoffBaseSeconds × 2^attempt`); after this many attempts a job becomes terminal `Failed`.

**Both workers start paused on boot** (to avoid surprise Apify/model spend). Use the **`/queues`** page to see pending counts + drain-time ETA, resume/pause each worker, prioritize a specific item (process-now), and add a YouTube URL to the processing queue. The Processing worker stops at scoring and enqueues a concept job per survivor; the Concept worker develops them on its own cadence — so a slow Concept model never blocks processing. No automated test runs the timed host loop or hits Apify/the model.
