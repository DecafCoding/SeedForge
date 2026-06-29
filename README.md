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

Phase 6 (Discovery — Close the Loop) is complete: an upload on a followed channel becomes a concept entirely on its own. A typed **`YouTubeDataClient`** (`Services/YouTube/`) resolves a channel reference — a `UC…` id, a `/channel/` URL, an `@handle` (via `forHandle`), or a legacy custom `/c/`·`/user/` reference (via a `search.list` fallback) — to its id, title, and uploads playlist, and lists recent uploads from that playlist (`ChannelRef.Parse` classifies the reference; the key is a query param on every call). A **`ChannelLibrary`** (`Features/Discovery/`) adds / lists / removes `Channel` rows deduped by a unique channel-id index, storing the uploads playlist id at add-time so each poll is a single call. The **PollChannels** slice lists recent ids, dedupes against existing `Video` rows, enqueues only the genuinely new ones to the `VideoQueue`, and stamps `LastPolledUtc` (dedupe by id is the correctness guarantee — no AI or transcript work). A daily **DiscoveryWorker** (`Workers/`) reuses the Phase 5 worker pattern (scope per tick, pause/wake, extracted `ProcessOnceAsync`, paused on boot) to poll the whole library, catching a per-channel failure so one bad channel never starves the rest. A **`/channels`** page manages the library and offers poll-now. Every automated test uses a stubbed handler; a real poll is key-gated and consumes YouTube quota.

Phase 5 (Queues & Workers — Unattended Runs) is complete: SeedForge now runs without you. Two durable, DB-backed queues (`Services/Queues/`) sit over the existing rows — a `Video` row *is* the video job, a `ConceptJob` row is the concept job — each with atomic claim (Pending→InProgress in a transaction), exponential **backoff** to a terminal `Failed` after `MaxAttempts`, pending-count, and **process-now** (jump the line + wake the worker). Two **`BackgroundService`** workers (`Workers/`) drain them on independent cadences: the **ProcessingWorker** ingests → segments → extracts → scores and **stops at the scoring seam**, enqueuing one `ConceptJob` per survivor (`Video` set `Done`, or `ProcessedNoIdeas` when zero survive — never built inline), while the **ConceptWorker** builds exactly one active `Concept` per job on its own cadence (honoring an optional profile/slot override), so a slow/paid Concept model never stalls the cheap local processing loop. Each worker is a singleton that opens a DI scope per tick and delegates to an extracted, unit-testable `ProcessOnceAsync`; a job error reschedules with backoff and never crashes the host. A `WorkerControl` singleton provides per-worker **pause/resume** and a wake signal. A bare **`/queues`** page shows pending counts + drain-time ETA with pause/resume, process-now, and URL enqueue. **Both workers start paused on boot** to avoid surprise Apify/model spend.

Phase 7 (Blazor UI & Operability) is complete — the final phase: the throwaway harness pages are consolidated into one coherent operational surface and the one genuinely new capability, the **cost & token dashboard**, is added. A read-only `CostDashboard` aggregation service (`Features/Observability/`) groups `AiCallLog` token usage + estimated cost **per stage** and **per provider** (local rig vs each hosted model), sums **Apify compute units** from transcripts, and applies a date filter — materializing then grouping in memory to sidestep SQLite `GroupBy` translation limits. A new **`/dashboard`** page surfaces those aggregates with a window selector; a consistent app shell + nav (Counter/Weather demo pages removed) reaches every route, and **`/`** is now a live overview (queue depths, active/stale concept counts, channels followed, recent concepts). The bare pages are polished into final form: **`/queues`** gains per-worker (Processing/Concept/Discovery) item-state tables (incl. `NoTranscript`, `Processed · 0 ideas`, `Failed` + attempts) over a pure, unit-tested `QueueEta` drain-time helper; **`/concepts`** adds full lineage (Concept → Idea → Segment → Transcript → Video) and side-by-side version compare; **`/config`** adds create/edit of a profile's five slots (keys never displayed/stored — blank ⇒ user-secrets); and a dedicated **`/replay`** page A/Bs a stored call against a chosen profile, original vs new, writing a new log and leaving the original untouched. No backend behavior changed — the phase is read-only queries + existing Phase 1–6 slice calls, validated by booting the app and asserting every route returns 200.

Phase 8 (Video Metadata Capture) is complete: each `Video` now carries the per-video metadata the pipeline previously discarded — **duration, view / like / comment counts, publish date, description, thumbnail, and YouTube channel id**, plus a `MetadataSource` (None/Apify/YouTube/Merged) + `MetadataFetchedAtUtc` provenance pair (nullable columns; a `VideoMetadata` migration applied at startup). Two sources feed it, both behind a pure, defensive parser: the **Apify** dataset item already stored on each transcript is re-parsed at ingest at **zero extra API cost** (`ApifyMetadataParser`), and an optional, batched YouTube **`videos.list`** call (`YouTubeDataClient.GetVideoMetadataAsync`, ≤50 ids = 1 quota unit) enriches newly discovered videos with fresher stats. An explicit merge rule (`VideoMetadataMerge`) resolves the two — YouTube wins the volatile counts, Apify fills gaps — and a one-shot **backfill** (a button on **`/queues`**) re-parses stored raw items for pre-existing videos. Metadata is best-effort throughout: a parse/fetch miss logs and continues, never failing ingestion or discovery, and `null` means "unknown", never zero. Display of these fields lands in later phases.

Phase 9 (Ideas Page) is complete: a read-only **`/ideas`** page lists every extracted idea in one pool-wide table — its text, source video, the latest score (four axes + mean), pass/fail (with **"unscored"** as a real third state), and creation date — so the operator can survey extraction/scoring quality across sources at a glance. It is backed by a new shared, read-only **`BrowseQueries`** service (`Features/Browse/`) whose `IdeaRowsAsync` resolves each idea's parent video through the `Idea → Segment → Transcript → Video` lineage (a left join keeps pasted-transcript ideas) and attaches the latest `IdeaScore` per idea (rescores accumulate; the newest wins). Each row links into the existing **`/concepts`** browser for full version history and to **`/videos/{id}`**; no backend behavior changed. `BrowseQueries` is shared with the sibling Videos / Video-Details pages in the following phases.

Phase 11 (Video Details Page) is complete: a read-only **`/videos/{id}`** page (the drill-down target of the Videos list) shows everything SeedForge knows about one video — identity (title, URL, channel, duration + Phase 8 metadata), lifecycle (status, date added, **date processed**, attempts, Apify cost, error), pipeline yield (segment count, idea split passed / failed / unscored, concepts), and the full **per-video AI trace** (stage / slot / model / tokens / cost, with totals) — linking back to the source and into the Concept Browser. `BrowseQueries.VideoDetailAsync` resolves the channel via `Video.ChannelId` (left join) with a `Transcript.ChannelName` fallback, reads the duration from `Video.DurationSeconds` (parsing the preserved Apify raw item only for pre-Phase-8 rows), splits ideas by latest score, and gathers the trace by the video's ideas' correlation ids. A new first-class **`Video.ProcessedAtUtc`** column (a `VideoProcessedAt` migration applied at startup) is stamped by the Processing worker on a terminal outcome (Done / ProcessedNoIdeas / NoTranscript); the page prefers it and falls back to a derived time (latest AI call, else transcript) — labelled **"derived"** — for videos processed before the column existed.

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
├─ Workers/                    # background workers (ProcessingWorker, ConceptWorker, DiscoveryWorker) + WorkerControl
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

### Failover

Optional, **off by default**: when a model endpoint is unreachable, the failed call is retried **once** against another profile's matching slot — e.g. fall back to **All OpenAI** when the local rig is down. Enable it on the **`/config`** page (a checkbox + a fallback-profile picker). Only **connectivity** failures trigger it — connection refused, request timeout, or HTTP 5xx; a 4xx or a bad/unparseable response does **not** (the model answered, so a retry would only double-spend). The fallback attempt is recorded as its own `AiCallLog`, so the per-video AI trace and cost dashboard show both the failed local call and the successful fallback. If the fallback resolves to the same endpoint as the primary, the retry is skipped. When failover is off (or also fails), the call surfaces its error and the existing worker backoff/retry applies as before.

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
  "DiscoveryIntervalSeconds": 86400,
  "MaxAttempts": 5,
  "BackoffBaseSeconds": 30
}
```

- `ProcessingIntervalSeconds` / `ConceptIntervalSeconds` / `DiscoveryIntervalSeconds` — how often each worker wakes (a process-now or enqueue wakes it early).
- `MaxAttempts` — failed jobs retry with exponential backoff (`BackoffBaseSeconds × 2^attempt`); after this many attempts a job becomes terminal `Failed`.

**Both workers start paused on boot** (to avoid surprise Apify/model spend). Use the **`/queues`** page to see pending counts + drain-time ETA, resume/pause each worker, prioritize a specific item (process-now), and add a YouTube URL to the processing queue. The Processing worker stops at scoring and enqueues a concept job per survivor; the Concept worker develops them on its own cadence — so a slow Concept model never blocks processing. No automated test runs the timed host loop or hits Apify/the model.

### Discovery (Channels)

The **DiscoveryWorker** polls a library of followed YouTube channels and enqueues new uploads — closing the loop so a new video becomes a concept with no manual URL. It uses the **YouTube Data API v3**; non-secret defaults ship under the `YouTube` section of `appsettings.json` (`BaseUrl`, `MaxResults`), and the **API key is blank in source** — supply it via user-secrets:

```bash
dotnet user-secrets set "YouTube:ApiKey" "AIza..."
```

The Discovery worker also **starts paused on boot** and runs on `Workers:DiscoveryIntervalSeconds` (default daily). Use the **`/channels`** page to add a channel (a `UC…` id, a `/channel/` URL, or an `@handle`), list / remove channels, resume the worker, or **poll now** for an on-demand poll. A poll lists each channel's recent uploads, enqueues only the ids with no existing `Video` row to the processing queue, and stamps `LastPolledUtc` — no transcript or AI work. Every automated test stubs the YouTube API; a real poll consumes YouTube quota.

When `YouTube:FetchVideoMetadata` is `true` (the default), a poll also makes one batched `videos.list` call (≤50 ids = 1 quota unit) to stamp metadata onto the newly discovered videos. Set it `false` to spend zero extra quota — discovery then behaves exactly as before:

```jsonc
"YouTube": { "FetchVideoMetadata": true }   // appsettings.json; or YOUTUBE__FetchVideoMetadata=false as an env var
```

### UI & Operability

The consolidated operational surface lets you run, monitor, and re-tune the whole engine without touching code. The app shell's navigation reaches every surface:

- **`/`** — home overview: video/concept queue depths, active & stale concept counts, channels followed, and recent concepts.
- **`/dashboard`** — **cost & token dashboard**: tokens + estimated cost aggregated **per stage** and **per provider** (local runs report tokens at cost 0; each hosted model separates out), grand totals, and **Apify compute units**, over a selectable window (default 30 days). Empty data renders zeros, not an error.
- **`/queues`** — per-worker (Processing / Concept / Discovery) pending counts, drain-time ETA, pause/resume, process-now, and recent-item state tables.
- **`/concepts`** — version history, active/stale flags, full lineage, side-by-side version compare, and the regenerate / rescore / replay actions.
- **`/ideas`** — read-only table of every extracted idea with its source video, latest axis scores, pass/fail, and creation date; rows drill into the Concept Browser.
- **`/videos`** — read-only table of every video and its pipeline yield: idea count split passed / failed / unscored (by each idea's latest score) and concept count (active / total), with status so non-yield outcomes (No transcript, Processed · 0 ideas) read clearly; rows link to the video's details.
- **`/videos/{id}`** — read-only details for one video: identity (title, URL, channel, duration + metadata), lifecycle (status, date added, date processed, attempts, Apify cost, error), pipeline yield (segments, idea split, concepts), and the full per-video AI trace with token/cost totals; links back to the source and into the Concept Browser. An unknown id renders "video not found", not an error.
- **`/config`** — manage profiles (create/edit the five slots, set active) and per-slot test-connection.
- **`/replay`** — A/B a stored call against a chosen profile, original vs new side by side.

The dashboard aggregation is read-only and groups in memory (small single-user data volumes), so it never fights EF/SQLite `GroupBy` translation.
