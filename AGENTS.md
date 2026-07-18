# FocusMed — Agent Instructions

Compact reference for AI sessions. Every fact here is verified against the current codebase.

> Audience split: `AGENTS.md` = you (AI sessions). `README.md` = humans onboarding. These files do not duplicate each other.

## Project Shape

Three projects, single dependency direction: `Worker` → `Dicom` → `Data` (leaf).

| Project | TFM | Role |
|---------|-----|------|
| `FocusMed.Data` | `net10.0` | EF Core (`FocusMedDbContext`), 11 entities, 4 enums (`StorageCommitmentStatus`, `PrintStatus`, `StudyStatus`, `AssociationOutcome`). 17+ indexes. No business logic. |
| `FocusMed.Dicom` | `net10.0` | `FocusMedScp` (single SCP), `DicomUpsertService`, hosted services, `PrintScuService`, `PrintExecutionService`, `StorageForwardService`. |
| `FocusMed.Worker` | `net10.0` | `Program.cs`, Serilog, DI wiring, `DicomListenerService`. |

Solution: `FocusMed.slnx` (XML, **not** classic `.sln`).

## Commands

```powershell
dotnet build
dotnet run --project src/FocusMed.Worker
```

- Terminal **must be Administrator** — binds TCP port `11112`.
- PostgreSQL on `localhost:5432`, database `focusmed`. `Database.Migrate()` runs on startup.
- New EF migration (from `src/FocusMed.Data`): `dotnet ef migrations add <Name> --project src/FocusMed.Data --startup-project src/FocusMed.Worker`.

## Runtime Data Layout

Data directory resolves in this order:
1. `FOCUSMED_DATA` environment variable (if set)
2. `%LOCALAPPDATA%\FocusMed` (default)

```
%LOCALAPPDATA%\FocusMed\
├── archive/
│   ├── <PatientName>_<Modality>_<YYYYMMDD>_<Hash>/{study-info.json, <SeriesUid>/<SopUid>.dcm}
│   └── <PatientName>_SC_<YYYYMMDD>_<Hash>/{study-info.json, <SeriesUid>/<SopUid>.dcm}
├── images/
│   └── <PatientName>_<Modality>_<YYYYMMDD>_<Hash>/<SeriesUid>/   # PNG per frame (on-demand)
└── logs/                                   # Serilog rolling + association log
```

Folders use `<Modality>` from DICOM tag (CT, MR, etc.) or `SC` for print images. All DICOM files stored in single `archive/` folder. Hash is 64-bit FNV-1a of `StudyInstanceUID`, rendered as 16-char uppercase hex.

## Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `FOCUSMED_DATA` | Override data directory | `%LOCALAPPDATA%\FocusMed` |
| `FOCUSMED_DB_CONNECTION` | Override PostgreSQL connection string | `Host=localhost;Port=5432;Database=focusmed;Username=postgres;Password=admin` |

## Explicitly Out of Scope

Until the user explicitly directs otherwise, **do not** build: PDF generation, installers/MSIs, `.docx` watchers.

## PHI Warning

`tools/real_test/` contains real DICOM files with patient names in filenames. `tools/` is gitignored, but PHI exposure during development is real.

## AI Agent Gotchas

Each item is anchored to a verified file:line. Cite these before touching the listed code.

1. **`app.manifest` is disabled** (`src/FocusMed.Worker/FocusMed.Worker.csproj:5`). UAC elevation is **not** automatic — you still need an Administrator terminal to bind port 11112.

2. **`CStoreScp` does not exist.** All DICOM roles live in `FocusMedScp` (`src/FocusMed.Dicom/FocusMedScp.cs`). Do not reference `CStoreScp` or invent a separate class.

3. **`PathHelper` checks `FOCUSMED_DATA` env var first**, then falls back to `%LOCALAPPDATA%\FocusMed`. No longer walks up looking for `FocusMed.slnx`. Archive directories are named `<PatientName>_<YYYYMMDD>_<Hash>` for human-readable browsing.

4. **Scope-per-request, not scope-per-singleton.** `DicomUpsertService` is a singleton but allocates a fresh `IServiceScope` (and `FocusMedDbContext`) **per file**. Concurrent C-STORE requests will collide on EF Core identity maps if you capture the DbContext in a field or convert the service to scoped.

5. **No `global.json`.** .NET 10.0 SDK is required but not pinned. Confirm the resolved SDK is 10.x before debugging build issues.

6. **All DICOM roles live in `FocusMedScp`.** Do not invent separate SCP classes for C-STORE, C-FIND, C-MOVE, or Print Management.

7. **Target framework: all projects are `net10.0`.** No Windows-specific APIs. fo-dicom uses ImageSharp (cross-platform). Build produces 0 warnings, 0 errors.

8. **fo-dicom transfer syntax names are non-standard.** Always pull `DicomTransferSyntax` / `DicomUID` static fields, never hand-type UIDs. The map in `FocusMedScp.cs` uses `JPEGProcess1`, `JPEGProcess2_4`, `JPEGProcess14`, `MPEG4AVCH264HighProfileLevel41` — not the human-friendly aliases.

9. **`DicomAssociation` exposes `RemoteHost`/`RemotePort` only** (`FocusMedScp.cs:71,133`). There is **no** `IPEndPoint` on the association — do not write `association.RemoteEndPoint`.

10. **Reject with `DicomRejectReason.NoReasonGiven`** (`FocusMedScp.cs:83,142`). The enum has no `Normal` member. There are two reject paths: AE whitelist denial (line 83) and zero presentation-contexts accepted (line 142).

11. **Storage Commitment needs per-site config.** `DicomNetworking.StorageCommitmentScuMapping` must list every calling AE that expects N-EVENT-REPORT callbacks. Without a mapping, N-ACTION is accepted but the device never receives confirmation.

12. **MWL entry condition is non-standard** (`FocusMedScp.cs:259`). C-FIND routes to MWL when `QueryRetrieveLevel` is empty **or** `ScheduledProcedureStepSequence` is present — not the strict `QueryRetrieveLevel == "WORKLIST"` check. MWL patient-name search uses `EF.Functions.Like` (line 269), not raw SQL. `OnCFindRequestAsync` delegates to `ExecuteCFindQueryAsync`; the outer method adds try/catch returning `DicomStatus.ProcessingFailure`. Test with real devices before assuming a non-standards-compliant SCU is broken.

13. **`Program.cs` re-binds `DicomNetworking` config** (`Program.cs:44-50`) into a fresh `DicomNetworkingOptions` so it can set `DicomServiceOptions.MaxPDULength`, instead of injecting `IOptions<DicomNetworkingOptions>` from DI. Works today; will silently diverge if the options class ever adds validation.

14. **Print execution is decoupled.** N-ACTION marks `PrintJob.Status = Completed` immediately upon receipt. Physical printing is triggered by `PrintExecutionService.ExecutePendingPrintJobAsync(id)` — not wired to any endpoint yet (out of scope). `PrintJob` has optional `PatientId`/`StudyId` nullable FKs — linked in N-SET after `IngestPrintImageAsync` creates the study, NOT in N-CREATE. N-CREATE always creates PrintJob with null FKs.

15. **`StorageCommitmentJob.Status` is an enum** (`StorageCommitmentStatus`), not a string. Values: `Pending=0`, `Completed=1`, `Failed=2`. Stored as `int` via `HasConversion<int>()`.

16. **Concurrent C-STORE race condition.** Multiple C-STORE requests for the same study use `ConcurrentDictionary<string, SemaphoreSlim>` per study UID in `DicomUpsertService` to serialize inserts. Duplicate studies are allowed — each C-STORE creates new Study/Series/DicomImage records.

17. **PostgreSQL DateTime UTC.** Npgsql requires `DateTimeKind.Utc`. `GetDicomDate()` returns `DateTime.SpecifyKind(date, DateTimeKind.Utc)`. Never use `DateTime.Now` or unspecified-kind DateTimes in entities.

18. **Association rejects when zero presentation contexts are accepted** (`FocusMedScp.cs:133-145`). After iterating all PCs, if `accepted == 0` the SCP sends `SendAssociationRejectAsync` with `DicomRejectResult.Permanent` / `DicomRejectReason.NoReasonGiven` and returns immediately — it never calls `SendAssociationAcceptAsync`. This prevents "successful" associations with no usable SOP classes.

19. **All three data-transfer handlers have try/catch for error resilience.** `OnCStoreRequestAsync` (line 200) returns `DicomStatus.ProcessingFailure` on exception. `OnCFindRequestAsync` (line 226) delegates to `ExecuteCFindQueryAsync` and returns `DicomStatus.ProcessingFailure` on failure. `OnCMoveRequestAsync` (line 397) wraps its DB query in try/catch and yields a failure response with `NumberOfFailedSuboperations=1`. None of these throw — they return DICOM error responses instead.

20. **N-SET, N-ACTION, and N-DELETE never throw exceptions to the DICOM framework.** All three construct their own `DicomDataset` command datasets (e.g. `DicomCommandField.NSetResponse`, line 710) and return explicit `DicomStatus.ProcessingFailure` responses from catch blocks. When building failure responses, they use the manual `DicomDataset` constructor with `CommandField`, `MessageIDBeingRespondedTo`, `Status`, and `CommandDataSetType` fields — not the request-based response constructor. This pattern prevents fo-dicom from logging unhandled exceptions on the network thread.

21. **`AssociationOutcome` has four values** (`AssociationAuditEntry.cs:15-21`): `Success=0`, `Rejected=1`, `Failed=2`, `PartiallyAccepted=3`. `PartiallyAccepted` is used at `FocusMedScp.cs:150` when at least one PC is accepted but at least one is also rejected. Do not assume an association is either fully accepted or fully rejected.

22. **Color vs. grayscale print is determined by `Association.PresentationContexts`** (`FocusMedScp.cs:603-607`), not by the request dataset alone. The SCP checks whether `BasicColorPrintManagementMeta` was accepted in any PC. As a fallback, `PrintPriority == "COLOR"` in the dataset also triggers color mode. If you change the PC negotiation list, verify that `isColor` still resolves correctly.

23. **N-DELETE eagerly loads the full delete cascade** (`FocusMedScp.cs:922-935`). The query uses `.Include(p => p.Patient).Include(p => p.FilmBoxes).ThenInclude(fb => fb.ImageBoxes).AsSplitQuery()` to load all child entities in separate SQL queries, then calls `RemoveRange` for each level. Do not remove the `Include` calls — doing so would cause N+1 queries or orphaned rows depending on cascade configuration.

24. **`PrintScuService` uses `TryGetSingleValue` for all DICOM tag access** (`PrintScuService.cs:166-178`). Never use `GetSingleValue<T>` on tags that might be absent — use `TryGetSingleValue<T>(tag, out var value)` or `GetSingleValueOrDefault(tag, defaultValue)` instead. Missing tags in source DICOM files are common (e.g., `BitsAllocated`, `PhotometricInterpretation`).

25. **N-SET's missing-SOP-UID guard returns a manual failure response** (`FocusMedScp.cs:707-719`). Before entering the try/catch, `OnNSetRequestAsync` checks for an empty `sopUid` and returns a `DicomNSetResponse` built from a raw `DicomDataset` with `Status = InvalidArgumentValue`. This is distinct from the catch-block failure path (line 750) which uses `ProcessingFailure`. Do not consolidate these into one path — they communicate different DICOM error codes to the SCU.

26. **NEVER generate UNKNOWN patient names.** All fallback paths use empty string `""` — never `UNKNOWN_<GUID>`. `StoreFileOnlyAsync` (line 38), N-SET handler (line 760), and `PngExtractionService` (line 168) all use `""` as fallback. UNKNOWN pollutes the DB with phantom patient records that confuse the dashboard.

27. **N-SET patient resolution chain** (`FocusMedScp.cs:720-763`): (1) FK chain: `imageBox→FilmBox→PrintJob→Patient`, (2) inner DICOM dataset: `PatientID`/`PatientName` from `BasicColorImageSequence`/`BasicGrayscaleImageSequence`, (3) most recent C-STORE study, (4) empty string. Never generates UNKNOWN.

28. **N-DELETE removes PrintJob from DB** (`FocusMedScp.cs:948`). Previously only marked `Status = Completed`, leaving orphaned rows. Now calls `db.PrintJobs.Remove(printJob)` after removing child FilmBoxes and ImageBoxes.

29. **StorageCommitmentScuService AE titles** (`StorageCommitmentScuService.cs:91,140`). `DicomClientFactory.Create` parameters are `_ourAet, job.CallingAet` (we are SCU, remote is SCP). Previously swapped — would cause strict SCPs to reject the association.

30. **FilmBox N-CREATE does not fall back to arbitrary PrintJob** (`FocusMedScp.cs:572-575`). When `ReferencedFilmSessionSequence` is missing or the referenced PrintJob is not found, logs a warning and creates an orphaned FilmBox. Previously fell back to `OrderByDescending(CreatedAt)` which picked up unrelated PrintJobs.

31. **N-ACTION with empty SOP UID does not complete arbitrary PrintJob** (`FocusMedScp.cs:875`). When both `sopUid` and `sopClassUid` are empty, logs a warning and returns Success without modifying any PrintJob. Previously fell back to completing the most recent PrintJob.

32. **N-DELETE with empty SOP UID returns `InvalidArgumentValue`** (`FocusMedScp.cs:923-930`). Previously returned `Success` for empty UIDs, which is incorrect per DICOM spec.

33. **N-SET returns `ProcessingFailure` when ImageBox not found** (`FocusMedScp.cs:786-794`). Previously returned `Success` even when the imageBox lookup failed, misleading the SCU.

34. **EF Core `MultipleCollectionIncludeWarning` — use `.AsSplitQuery()`.** Any query with 2+ collection navigations (e.g., `Include(Series).ThenInclude(Images)` + `Include(Frames)`) triggers this warning. Add `.AsSplitQuery()` to split into separate SQL queries. Fixed in `StudyCompletionService.cs:51`, `PngExtractionService.cs:51`, `FocusMedScp.cs:945`.

35. **PNG extraction is on-demand, not automatic.** PNGs are only generated when a viewer calls `GetOrExtractFramesAsync(studyId)`. C-STORE and study completion do NOT extract PNGs. This keeps the receive pipeline fast and avoids CPU spikes. `PngCleanupService` deletes stale PNGs after 60min.

36. **C-STORE now returns `ProcessingFailure` on DB/save errors.** `StoreFileOnlyAsync` (`DicomUpsertService.cs:164`) re-throws exceptions. `OnCStoreRequestAsync` catches them and returns `DicomStatus.ProcessingFailure`. Previously swallowed exceptions silently succeeded — the sender would never retry. Also cleans up orphaned `.dcm` files on failure.

37. **PrintScuService two-phase send** (`PrintScuService.cs`). Phase 1 sends all N-CREATE requests (FilmSession + ImageBoxes) and calls `SendAsync()` to populate SCP-assigned UIDs via `OnResponseReceived` callbacks. Phase 2 opens a new `DicomClient` and sends N-SET + N-ACTION + N-DELETE with the resolved UIDs. The old single-phase approach read closure variables before `SendAsync` fired callbacks, so all SCP UIDs were lost.

38. **StorageCommitmentScuService only marks Completed if N-EVENT-REPORT was sent.** `SendNEventReportAsync` returns `bool` (`StorageCommitmentScuService.cs:68`). When no AET mapping exists, the job stays `Pending` and a warning is logged. Previously marked `Completed` even when the send silently returned.

39. **PrintScuService + PrintExecutionService are registered in DI** (`DependencyInjection.cs:19-20`). Both are `AddSingleton`. Any code path resolving `IPrintScuService` or `PrintExecutionService` would crash before this fix.

40. **PngExtractionService `_studyLocks`/`_studyRefCount` cleanup** (`PngExtractionService.cs:211`). When refcount reaches 0, both the semaphore and refcount entry are removed from their static dictionaries. Previously `_studyLocks.TryRemove` was called immediately after `Release()`, allowing another thread to acquire a semaphore that was about to be deleted. Now removal is conditional on `remaining <= 0`.

41. **DicomUpsertService `_studyLocks` cleanup** (`DicomUpsertService.cs:168`). When the semaphore's `CurrentCount > 0` (no waiters), the entry is removed from the static dictionary. Prevents unbounded memory growth from one `SemaphoreSlim` per unique StudyInstanceUID.

42. **StorageForwardQueue `PendingCount` only increments on successful enqueue** (`StorageForwardQueue.cs:26`). `TryWrite` return value is checked before `Interlocked.Inrement`. Previously inflated the counter even when the channel was completed/full.

43. **Data directory resolution uses `FOCUSMED_DATA` env var directly** (`DicomUpsertService.cs:24`, `PngExtractionService.cs:33`). Both services read `Environment.GetEnvironmentVariable("FOCUSMED_DATA")` with fallback to `%LOCALAPPDATA%\FocusMed`. Previously read a non-existent `DataDirectory` config key. `IConfiguration` parameter removed from `DicomUpsertService` constructor.

44. **StudyCompletionService re-checks image count before marking Complete** (`StudyCompletionService.cs:60`). After querying ready studies, re-counts images per study. If the count changed (C-STORE arrived during the query), the study's `LastUpdatedAt` is bumped and completion is deferred. Prevents marking a study Complete while images are still arriving.

45. **N-DELETE returns `ProcessingFailure` when PrintJob not found** (`FocusMedScp.cs:947`). Previously returned `Success` even when the SOP UID matched no PrintJob, misleading the SCU.

46. **PngCleanupService batched query** (`PngCleanupService.cs:114`). Replaces N+1 per-image COUNT queries with a single `GroupBy` + `Except` to find images with zero remaining PNGs.

47. **StorageCommitmentScuService single query** (`StorageCommitmentScuService.cs:58`). Merged redundant `CountAsync` + `ToDictionaryAsync` into a single `ToDictionaryAsync` with `.Count` on the result.

48. **`DicomHelpers.SanitizeFileName` returns `""` not `"UNKNOWN"`** (`DicomHelpers.cs:5`). Also strips `\` and `/` characters to prevent path traversal. Empty input returns empty string per AGENTS.md #26.

49. **`DicomHelpers.GetDicomDate` trims whitespace and uses `CultureInfo.InvariantCulture`** (`DicomHelpers.cs:26`). Locale-dependent parsing was a latent bug. Returns `null` for empty/whitespace-only input.

## Quick File Index

- `src/FocusMed.Worker/Program.cs` — entry, Serilog, DI wiring, startup config summary, migration.
- `src/FocusMed.Worker/DicomListenerService.cs` — starts `IDicomServer<FocusMedScp>`.
- `src/FocusMed.Worker/PathHelper.cs` — resolves data directory (FOCUSMED_DATA or %LOCALAPPDATA%/FocusMed).
- `src/FocusMed.Worker/appsettings.json` — config.
- `src/FocusMed.Dicom/FocusMedScp.cs` — every DICOM role.
- `src/FocusMed.Dicom/DicomUpsertService.cs` — ingestion (UID repair, FNV-1a, forward queue enqueue).
- `src/FocusMed.Dicom/PngExtractionService.cs` — on-demand PNG extraction with refcount tracking.
- `src/FocusMed.Dicom/PngCleanupService.cs` — BackgroundService, deletes stale PNGs every 15min.
- `src/FocusMed.Dicom/Options/PngExtractionOptions.cs` — Enabled, CleanupIntervalMinutes, MaxAgeMinutes.
- `src/FocusMed.Dicom/StudyCompletionService.cs` — 5s polling loop.
- `src/FocusMed.Dicom/StorageCommitmentScuService.cs` — 10s polling loop, N-EVENT-REPORT with DB-backed SOP Class lookup.
- `src/FocusMed.Dicom/PrintScuService.cs` — DICOM Print SCU (N-CREATE/SET/ACTION/DELETE).
- `src/FocusMed.Dicom/PrintExecutionService.cs` — decoupled print execution (pending→printing→completed/failed).
- `src/FocusMed.Dicom/StorageForwardQueue.cs` — `Channel<T>`-based forward queue with `Complete()`/`PendingCount` for graceful shutdown.
- `src/FocusMed.Dicom/StorageForwardService.cs` — hosted C-STORE SCU, drains queue on `StopAsync`.
- `src/FocusMed.Dicom/Options/DicomNetworkingOptions.cs` — `DicomNetworking` section binding + `FilmPrinters`, `StorageForwardTargets`.
- `src/FocusMed.Dicom/DicomHelpers.cs` — Static helpers: SanitizeFileName, GetFnv1aHash, GetDicomDate.
- `src/FocusMed.Data/FocusMedDbContext.cs` — 11 DbSets, fluent FK config, enum conversion, 17 indexes.
- `src/FocusMed.Data/Entities/StorageCommitmentStatus.cs` — `Pending=0`, `Completed=1`, `Failed=2`.
- `src/FocusMed.Data/Migrations/` — 11 EF migrations (latest: `RemoveUnusedIndexesAndPatientCreatedAt`).
