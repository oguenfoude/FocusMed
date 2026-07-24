# FocusMed — Agent Instructions

Compact reference for AI sessions. Every fact here is verified against the current codebase.

> Audience split: `AGENTS.md` = you (AI sessions). `README.md` = humans onboarding. These files do not duplicate each other.

> ⚠️ **Dashboard printing was intentionally removed in this iteration.** The Dashboard `PrintService` (SumatraPDF-based printing on Konica) was deleted because it had tray error issues and the user opted to defer printing entirely. DICOM Print Management (N-CREATE/SET/ACTION/DELETE in `FocusMedScp`) remains — that's a separate system at the DICOM protocol level, not the Dashboard. PDF preview iframe still works for browsing.

## Project Shape

Five projects. Dependency direction: `Worker` → `Dicom` → `Data` (leaf). `Dashboard` → `Data` + `Dicom`. `PrintCapture` → `Data`.

| Project | TFM | Role |
|---------|-----|------|
| `FocusMed.Data` | `net10.0` | EF Core (`FocusMedDbContext`), 11 DbSets, 4 enums, 20 migrations. No business logic. |
| `FocusMed.Dicom` | `net10.0` | `FocusMedScp` (single SCP, all DICOM roles), `DicomUpsertService`, hosted services, `PrintScuService`, `PrintExecutionService`, `StorageForwardService`. |
| `FocusMed.Worker` | `net10.0` | `Program.cs`, Serilog, DI wiring, `DicomListenerService`. Headless DICOM listener. |
| `FocusMed.Dashboard` | `net10.0` | Blazor Server UI (`InteractiveServer`). Razor components, `PngExtractionService`, `PdfService`, `StudyService`. HTTP `:5000`. **No Dashboard printing — DICOM-side Print Management only.** |
| `FocusMed.PrintCapture` | `net10.0-windows` | WPF desktop app. Creates "FocusMed" virtual printer on Windows, monitors print output, converts to PDF, shows native popup for study selection. |

Solution: `FocusMed.slnx` (XML, **not** classic `.sln`).

## Commands

```powershell
dotnet build
dotnet run --project src/FocusMed.Worker
```

- Terminal **must be Administrator** — binds TCP port `11112`.
- PostgreSQL on `localhost:5432`, database `focusmed`. `Database.Migrate()` runs on startup.
- New EF migration (from `src/FocusMed.Data`): `dotnet ef migrations add <Name> --project src/FocusMed.Data --startup-project src/FocusMed.Worker`.

### Dashboard

```powershell
dotnet run --project src/FocusMed.Dashboard    # HTTP :5000
```

- **`dotnet watch` lock issue**: `dotnet watch` on Dashboard fails if a previous Dashboard process is still running (file lock on `apphost.exe`). Kill the process by name or PID before restarting: `Stop-Process -Name "FocusMed.Dashboard" -Force`.
- Dashboard uses Blazor Server (`InteractiveServer` render mode). All UI is in French.
- Dashboard does NOT run DICOM listener — that's the Worker's job. Run both separately.

## Runtime Data Layout

Data directory resolves in this order:
1. `FOCUSMED_DATA` environment variable (if set)
2. `%LOCALAPPDATA%\FocusMed` (default)

```
%LOCALAPPDATA%\FocusMed\
├── archive/
│   ├── <PatientName>_<Modality>_<YYYYMMDD>/{study-info.json, <SeriesUid>/<SopUid>.dcm}
│   └── <PatientName>_SC_<YYYYMMDD>/{study-info.json, <SeriesUid>/<SopUid>.dcm}
├── images/
│   └── <PatientName>_<Modality>_<YYYYMMDD>/<SeriesUid>/   # PNG per frame (on-demand)
├── pdf-cache/                                # Generated PDFs (60min TTL, auto-cleaned)
└── logs/                                   # Serilog rolling + association log
```

Folders use `<Modality>` from DICOM tag (CT, MR, etc.) or `SC` for print images. All DICOM files stored in single `archive/` folder. Folder lookup uses `DicomImage.FilePath` from DB + `Directory.GetParent()` — never hash substring matching.

## Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `FOCUSMED_DATA` | Override data directory | `%LOCALAPPDATA%\FocusMed` |
| `FOCUSMED_DB_CONNECTION` | Override PostgreSQL connection string | `Host=localhost;Port=5432;Database=focusmed;Username=postgres;Password=admin` |

## Explicitly Out of Scope

Until the user explicitly directs otherwise, **do not** build: installers/MSIs, `.docx` watchers.

## PHI Warning

`tools/real_test/` contains real DICOM files with patient names in filenames. `tools/` is gitignored, but PHI exposure during development is real.

## AI Agent Gotchas

Each item is anchored to a verified file:line. Cite these before touching the listed code.

1. **`app.manifest` is disabled** (`src/FocusMed.Worker/FocusMed.Worker.csproj:5`). UAC elevation is **not** automatic — you still need an Administrator terminal to bind port 11112.

2. **`CStoreScp` does not exist.** All DICOM roles live in `FocusMedScp` (`src/FocusMed.Dicom/FocusMedScp.cs`). Do not reference `CStoreScp` or invent a separate class.

3. **`PathHelper` checks `FOCUSMED_DATA` env var first**, then falls back to `%LOCALAPPDATA%\FocusMed`. No longer walks up looking for `FocusMed.slnx`. Archive directories are named `<PatientName>_<Modality>_<YYYYMMDD>` for human-readable browsing.

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

35. **PNG extraction is on-demand, not automatic.** PNGs are only generated when a viewer calls `GetOrExtractFramesAsync(studyId)`. C-STORE and study completion do NOT extract PNGs. This keeps the receive pipeline fast and avoids CPU spikes. PNGs persist on disk permanently once extracted.

36. **C-STORE now returns `ProcessingFailure` on DB/save errors.** `StoreFileOnlyAsync` (`DicomUpsertService.cs:164`) re-throws exceptions. `OnCStoreRequestAsync` catches them and returns `DicomStatus.ProcessingFailure`. Previously swallowed exceptions silently succeeded — the sender would never retry. Also cleans up orphaned `.dcm` files on failure.

37. **PrintScuService two-phase send** (`PrintScuService.cs`). Phase 1 sends all N-CREATE requests (FilmSession + ImageBoxes) and calls `SendAsync()` to populate SCP-assigned UIDs via `OnResponseReceived` callbacks. Phase 2 opens a new `DicomClient` and sends N-SET + N-ACTION + N-DELETE with the resolved UIDs. The old single-phase approach read closure variables before `SendAsync` fired callbacks, so all SCP UIDs were lost.

38. **StorageCommitmentScuService only marks Completed if N-EVENT-REPORT was sent.** `SendNEventReportAsync` returns `bool` (`StorageCommitmentScuService.cs:68`). When no AET mapping exists, the job stays `Pending` and a warning is logged. Previously marked `Completed` even when the send silently returned.

39. **PrintScuService + PrintExecutionService are registered in DI** (`DependencyInjection.cs:19-20`). Both are `AddSingleton`. Any code path resolving `IPrintScuService` or `PrintExecutionService` would crash before this fix.

40. **PngExtractionService `_studyLocks`/`_studyRefCount` cleanup** (`PngExtractionService.cs:211`). When refcount reaches 0, both the semaphore and refcount entry are removed from their static dictionaries. Previously `_studyLocks.TryRemove` was called immediately after `Release()`, allowing another thread to acquire a semaphore that was about to be deleted. Now removal is conditional on `remaining <= 0`. Refcount is only managed by `GetOrExtractFramesAsync` (increment) and `ReleaseStudyPng` (decrement) — `ExtractForImageAsync` does NOT touch refcount.

41. **DicomUpsertService `_studyLocks` cleanup** (`DicomUpsertService.cs:168`). When the semaphore's `CurrentCount > 0` (no waiters), the entry is removed from the static dictionary. Prevents unbounded memory growth from one `SemaphoreSlim` per unique StudyInstanceUID.

42. **StorageForwardQueue `PendingCount` only increments on successful enqueue** (`StorageForwardQueue.cs:26`). `TryWrite` return value is checked before `Interlocked.Inrement`. Previously inflated the counter even when the channel was completed/full.

43. **Data directory resolution uses `FOCUSMED_DATA` env var directly** (`DicomUpsertService.cs:24`, `PngExtractionService.cs:33`). Both services read `Environment.GetEnvironmentVariable("FOCUSMED_DATA")` with fallback to `%LOCALAPPDATA%\FocusMed`. Previously read a non-existent `DataDirectory` config key. `IConfiguration` parameter removed from `DicomUpsertService` constructor.

44. **StudyCompletionService re-checks image count before marking Complete** (`StudyCompletionService.cs:60`). After querying ready studies, re-counts images per study. If the count changed (C-STORE arrived during the query), the study's `LastUpdatedAt` is bumped and completion is deferred. Prevents marking a study Complete while images are still arriving.

45. **N-DELETE returns `ProcessingFailure` when PrintJob not found** (`FocusMedScp.cs:947`). Previously returned `Success` even when the SOP UID matched no PrintJob, misleading the SCU.

46. **`DicomHelpers.SanitizeFileName` returns `""` not `"UNKNOWN"`** (`DicomHelpers.cs:5`). Also strips `\` and `/` characters to prevent path traversal. Empty input returns empty string per AGENTS.md #26.

47. **`DicomHelpers.GetDicomDate` trims whitespace and uses `CultureInfo.InvariantCulture`** (`DicomHelpers.cs:26`). Locale-dependent parsing was a latent bug. Returns `null` for empty/whitespace-only input.

48. **Duplicate C-STORE after Complete creates new study with shared UID** (`DicomUpsertService.cs:96-108`). When SOP UID exists in a completed/archived study, a new study UID is generated. `_newStudyAfterCompleteCache` ensures all C-STORE images within 5 min for same patient+date share the same new UID (prevents 1-image-per-study fragmentation).

49. **Reverse merge captures PRINT directory BEFORE moving files** (`DicomUpsertService.cs:671-673`). The PRINT study directory path must be captured before the move loop updates `img.FilePath`. Otherwise the cleanup deletes the C-STORE study directory.

50. **N-DELETE handles concurrent delete from merge** (`FocusMedScp.cs:957-965`). Catches `DbUpdateConcurrencyException` when reverse merge already deleted the PrintJob. Returns Success instead of ProcessingFailure.

51. **PdfService resolves PDF cache directory from `FOCUSMED_DATA` env var** (`PdfService.cs:15-17`). Cover page uses MiniPdf to convert `cover.docx` (with `{{PatientName}}`/`{{StudyDate}}` placeholders replaced via ZipArchive) to PDF. Image pages use QuestPDF. Final PDF merged via PdfSharpCore. If `FOCUSMED_DATA` is unset, falls back to `%LOCALAPPDATA%\FocusMed`.

52. **PngExtractionService refcount is only in `GetOrExtractFramesAsync` and `ReleaseStudyPng`.** `ExtractForImageAsync` (`PngExtractionService.cs:144`) acquires a per-study semaphore for thread safety but does NOT touch `_studyRefCount`. Do NOT add refcount management to `ExtractForImageAsync` — it would cause double-decrement since the caller already manages refcount.

53. **`Archives.razor` and `DeletedStudies.razor` must declare `@implements IDisposable`** (`Archives.razor:3`, `DeletedStudies.razor:3`). Both create `Timer` instances in `OnInitializedAsync`. Without `IDisposable`, the timer leaks on navigation (Blazor disposes the component but the timer's CLR reference keeps firing, causing `ObjectDisposedException`).

54. **`StudyDetails.razor` has no `@inject NavigationManager`** and no `@using FellowOakDicom`. Both were removed as dead code. The page navigates via `<a href>` links, not `Navigation.NavigateTo()`.

55. **Data layer has 4 enums.** `StorageCommitmentStatus` (Pending/Completed/Failed), `PrintStatus` (Pending/Printing/Completed/Failed), `StudyStatus` (Receiving/Complete/Failed/Archived/Deleted), `AssociationOutcome` (Success/Rejected/Failed/PartiallyAccepted). `PrinterType` (GrayLevel/Multicolor) is in `FocusMed.Dicom.Options`, not the Data layer.

56. **`BackfillMetadataAsync` runs on startup** (`Program.cs:98`). On first boot or after adding the metadata migration, `DicomUpsertService.BackfillMetadataAsync()` iterates all DICOM images and backfills missing metadata (BirthDate, Sex, Description, AccessionNumber, InstitutionName, Manufacturer, ReferringPhysicianName) from the `.dcm` files into the database. This is a one-time operation per image.

57. **`StudyStabilizationSeconds` config key** (`appsettings.json:34`, default 60). Controls how long since `LastUpdatedAt` before a study is considered stable and marked Complete. `StudyCompletionService` polls every 5 seconds and marks studies as Complete when `LastUpdatedAt <= UtcNow - stabilizationSeconds` and no new images arrive.

58. **`FocusMedDbContextFactory`** (`FocusMedDbContextFactory.cs`) is the `IDesignTimeDbContextFactory` for EF migrations. Uses a hardcoded connection string (reads from `FOCUSMED_DB_CONNECTION` env var). Not registered in DI — only used by `dotnet ef` tooling.

59. **Dashboard `/pdf-cache` serves with `ServeUnknownFileTypes = true`** (`Program.cs:61-67`). This is intentional — PDFs are served by filename only (no extension in URL path). The `DefaultContentType` is `application/pdf`. Do not change `ServeUnknownFileTypes` to `false` or PDFs won't load in the iframe.

60. **`_mergeLocks` is a separate static dictionary from `_studyLocks`** (`DicomUpsertService.cs:18`). `_studyLocks` serializes per-study C-STORE inserts. `_mergeLocks` serializes forward/reverse merge operations. They use the same `ConcurrentDictionary<string, SemaphoreSlim>` pattern but must not be consolidated — merge and insert are independent operations that can safely overlap.

61. **`DicomImage.Source` defaults to `"C-STORE"`** (`DicomImage.cs:10`). Populated by `DicomUpsertService` — value is `"C-STORE"` for ingested images. Used to distinguish image origin.

62. **PrintCapture uses Local Port, not PORTPROMPT:** (`PrinterSetupService.cs`, `appsettings.json`). The "FocusMed" printer uses `Microsoft Print To PDF` driver with `Local Port` named `C:\FocusMed_Prints\incoming.pdf`. The Local Port writes PDF output directly to that file with no dialog. `PrintJobMonitorService` uses a **500ms `Timer` poller** (NOT `FileSystemWatcher` — spooler writes via `localspl.dll` which `FileSystemWatcher` can't see). Polls `incoming.pdf` every 500ms, waits 500ms for size stabilization, then copies to `resumes/` and fires the popup. Total detection: ~1 second. Do NOT change the port back to `PORTPROMPT:` — that shows a Windows save dialog.

62b. **PrintCapture popup lists ALL non-deleted studies, not just unassigned.** (`DatabaseService.GetSelectableStudiesAsync` — `resumes\DatabaseService.cs:20-36` and `ResumePickerWindow.xaml.cs:33`). The popup shows every study, with a violet "Resume" badge in the "Statut" column when `Study.ResumePdfPath != null`. Picking a study that already has a resume triggers a `MessageBox` confirmation "Cette etude a deja un resume associe... L'ancien fichier PDF sera supprime definitivement." Selecting "Yes" calls `AssignResumeAsync` which captures the OLD path, deletes the old file from disk, then assigns the new path. **This was a critical UX fix**: previously only unassigned studies showed in the list, so 2nd-print popups appeared empty (or filtered out the user's intended study).

63. **PrintCapture process lock issue.** `FocusMed.PrintCapture.exe` can be locked by running processes in other user sessions. Build fails with MSB3027 when the exe is in use. Workaround: rename the locked exe before building, or kill the process first. `Stop-Process -Name "FocusMed.PrintCapture" -Force` may fail with access denied if the process is in another session.

64. **Unlink Resume button on StudyDetails** (`StudyDetails.razor:89-102`). A red pill button "Retirer" appears next to the violet `Résumé` badge when `Study.ResumePdfPath != null`. Click opens a `ConfirmModal` asking "Le fichier PDF du resume sera supprime definitivement et ne pourra plus etre reassocie a cette etude. Continuer ?". On confirm, `StudyService.UnlinkResumeAsync(Study.Id)` (`StudyService.cs:116-148`) deletes the PDF from `%LOCALAPPDATA%\FocusMed\{study.ResumePdfPath}` AND clears `Study.ResumePdfPath` (file delete is wrapped in try/catch — DB update always succeeds even if the file is already gone). Uses `ToastNotification` for success/error feedback. After unlink, triggers `RegeneratePdfPreviewAsync()` to refresh the iframe without the resume page.

## Quick File Index

- `src/FocusMed.Worker/Program.cs` — entry, Serilog, DI wiring, startup config summary, migration.
- `src/FocusMed.Worker/DicomListenerService.cs` — starts `IDicomServer<FocusMedScp>`.
- `src/FocusMed.Worker/PathHelper.cs` — resolves data directory (FOCUSMED_DATA or %LOCALAPPDATA%/FocusMed).
- `src/FocusMed.Worker/appsettings.json` — config.
- `src/FocusMed.Dicom/FocusMedScp.cs` — every DICOM role.
- `src/FocusMed.Dicom/DicomUpsertService.cs` — ingestion (UID repair, forward queue enqueue, duplicate-after-complete cache, forward/reverse merge).
- `src/FocusMed.Dicom/PngExtractionService.cs` — on-demand PNG extraction with refcount tracking.
- `src/FocusMed.Dicom/Options/PngExtractionOptions.cs` — Enabled flag only.
- `src/FocusMed.Dicom/StudyCompletionService.cs` — 5s polling loop.
- `src/FocusMed.Dicom/StorageCommitmentScuService.cs` — 10s polling loop, N-EVENT-REPORT with DB-backed SOP Class lookup.
- `src/FocusMed.Dicom/PrintScuService.cs` — DICOM Print SCU (N-CREATE/SET/ACTION/DELETE).
- `src/FocusMed.Dicom/IPrintScuService.cs` — Interface for PrintScuService.
- `src/FocusMed.Dicom/PrintExecutionService.cs` — decoupled print execution (pending→printing→completed/failed).
- `src/FocusMed.Dicom/StorageForwardQueue.cs` — `Channel<T>`-based forward queue with `Complete()`/`PendingCount` for graceful shutdown.
- `src/FocusMed.Dicom/StorageForwardService.cs` — hosted C-STORE SCU, drains queue on `StopAsync`.
- `src/FocusMed.Dicom/Options/DicomNetworkingOptions.cs` — `DicomNetworking` section binding + `FilmPrinters`, `StorageForwardTargets`.
- `src/FocusMed.Dicom/DicomHelpers.cs` — Static helpers: SanitizeFileName, GetFnv1aHash, GetDicomDate.
- `src/FocusMed.Data/FocusMedDbContext.cs` — 11 DbSets, fluent FK config, enum conversion, 20 indexes.
- `src/FocusMed.Data/FocusMedDbContextFactory.cs` — `IDesignTimeDbContextFactory` for EF migrations.
- `src/FocusMed.Data/DependencyInjection.cs` — `AddFocusMedData` extension method, registers `FocusMedDbContext` with Npgsql.
- `src/FocusMed.Data/Entities/StorageCommitmentStatus.cs` — `Pending=0`, `Completed=1`, `Failed=2`.
- `src/FocusMed.Data/Migrations/` — 20 EF migrations (latest: `AddStudyResumePath`).
- `src/FocusMed.Dashboard/Program.cs` — Blazor Server entry, registers `PngExtractionService`, serves `/images` from data dir.
- `src/FocusMed.Dashboard/Components/Pages/Home.razor` — studies list, search, date filter, pagination, delete modal.
- `src/FocusMed.Dashboard/Components/Pages/StudyDetails.razor` — patient info, study metadata, PNG image grid viewer.
- `src/FocusMed.Dashboard/Components/Pages/Archives.razor` — `/archives` route, archived studies with search/filters/pagination.
- `src/FocusMed.Dashboard/Components/Pages/DeletedStudies.razor` — `/deleted` route, deleted studies with restore/permanent delete.
- `src/FocusMed.Dashboard/Components/Layout/MainLayout.razor` — nav bar (Études, Archives, Supprimées), server status badge.
- `src/FocusMed.Dashboard/Components/Shared/ToastNotification.razor` — toast notification component.
- `src/FocusMed.Dashboard/Components/Shared/ConfirmModal.razor` — confirmation modal with French defaults.
- `src/FocusMed.Dashboard/Services/PdfService.cs` — PDF **preview** generation (MiniPdf: cover from `wwwroot/cover.docx` template with placeholder replacement + optional resume PDF + QuestPDF A4 portrait one image per page + PdfSharpCore PDF merge). Only used by the iframe on `StudyDetails.razor`. Registered as Scoped. **Dashboard does NOT print PDFs to physical printers.**
- `src/FocusMed.Dashboard/Services/StudyService.cs` — shared delete/soft-delete/restore/archive/unlink-resume logic. Registered as Scoped.
- `src/FocusMed.Dashboard/Services/DeletedCleanupService.cs` — BackgroundService, auto-deletes Deleted studies after 30 days.
- `src/FocusMed.PrintCapture/FocusMed.PrintCapture.csproj` — WPF app (.NET 10-windows), references FocusMed.Data.
- `src/FocusMed.PrintCapture/App.xaml.cs` — WPF entry point, DI setup, printer creation, file monitoring. Starts hidden, window appears on print only.
- `src/FocusMed.PrintCapture/Services/PrinterSetupService.cs` — creates "FocusMed" virtual printer on Windows using Local Port `C:\FocusMed_Prints\incoming.pdf` (no dialog — PDF auto-saved).
- `src/FocusMed.PrintCapture/Services/PrintJobMonitorService.cs` — Timer poller on `incoming.pdf` (every 500ms), waits 500ms for size stabilization, copies to `resumes/` folder, fires popup. Total detection ~1 second.
- `src/FocusMed.PrintCapture/Services/DatabaseService.cs` — EF Core queries for unassigned studies + resume assignment.
- `src/FocusMed.PrintCapture/Windows/ResumePickerWindow.xaml` — WPF popup for study selection.
- `src/FocusMed.PrintCapture/Windows/ResumePickerWindow.xaml.cs` — code-behind for picker window.
