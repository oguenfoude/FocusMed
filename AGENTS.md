# FocusMed — Agent Instructions

Compact reference for AI sessions. Every fact here is verified against the current codebase.

> Audience split: `AGENTS.md` = you (AI sessions). `README.md` = humans onboarding. These files do not duplicate each other.

## Project Shape

**Seven** projects. Dependency direction: `Worker` → `Dicom` → `Data` (leaf). `Dashboard` → `Data` + `Dicom` + `Printing`. `Launcher` → `Data`. `Printing` has no project dependencies.

| Project | TFM | Role |
|---------|-----|------|
| `FocusMed.Data` | `net10.0` | EF Core (`FocusMedDbContext`), 11 DbSets, 4 enums, 1 migration. No business logic. |
| `FocusMed.Dicom` | `net10.0` | `FocusMedScp` (single SCP, all DICOM roles), `DicomUpsertService`, hosted services, `StorageForwardService`. |
| `FocusMed.Worker` | `net10.0` | `Program.cs`, Serilog, DI wiring, `DicomListenerService`. Headless DICOM listener. |
| `FocusMed.Dashboard` | `net10.0-windows` | Blazor Server UI (`InteractiveServer`). Razor components, `PngExtractionService`, `PdfService`, `StudyService`. HTTP `:5000`. |
| `FocusMed.Printing` | `net10.0-windows` | Print engine (`PrintExecutionService`, `BookletImpositionService`, capability discovery, raw/XPS output). UI-independent. |
| `FocusMed.Printing.TestConsole` | `net10.0-windows` | Console smoke-test harness for the print engine. |
| `FocusMed.Launcher` | `net10.0-windows` | WPF supervisor app (tray / resume-picker popup / "FocusMed" virtual printer / print monitor) that starts & watches `FocusMed.Worker` + `FocusMed.Dashboard` as hidden children, always restarting them. Absorbed the former `FocusMed.PrintCapture`. |

Solution: `FocusMed.slnx` (XML, **not** classic `.sln`).

## Commands

```powershell
dotnet build
dotnet run --project src/FocusMed.Worker
```

- Terminal **must be Administrator** — binds TCP port `11112`.
- SQLite database `focusmed.db` in working directory. `Database.Migrate()` runs on startup.
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
│   └── covers/                               # Cover page cache (24h TTL)
├── resumes/                                  # Launcher resume PDFs
└── logs/                                   # Serilog rolling + association log
```

Folders use `<Modality>` from DICOM tag (CT, MR, etc.) or `SC` for print images. All DICOM files stored in single `archive/` folder. Folder lookup uses `DicomImage.FilePath` from DB + `Directory.GetParent()` — never hash substring matching.

Dashboard startup auto-provisions `cover.docx` and `cover-logo.jpg` from `wwwroot/` into the data dir if missing (`Program.cs`).

## Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `FOCUSMED_DATA` | Override data directory | `%LOCALAPPDATA%\FocusMed` |
| `FOCUSMED_DB_CONNECTION` | Override SQLite connection string | `Data Source=focusmed.db` |

## Explicitly Out of Scope

Until the user explicitly directs otherwise, **do not** build `.docx` watchers. (Installers are now in-scope for client deployment — the Inno Setup script lives in `installer/FocusMed.iss`; the >100 MB built exe is gitignored.)

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

7. **Target frameworks: mixed `net10.0` and `net10.0-windows`.** `FocusMed.Data`, `FocusMed.Dicom`, `FocusMed.Worker` are cross-platform `net10.0`. `FocusMed.Dashboard`, `FocusMed.Printing`, `FocusMed.Printing.TestConsole`, `FocusMed.Launcher` are `net10.0-windows` (System.Printing, WPF, XPS). Build produces 0 warnings, 0 errors.

8. **fo-dicom transfer syntax names are non-standard.** Always pull `DicomTransferSyntax` / `DicomUID` static fields, never hand-type UIDs. The map in `FocusMedScp.cs` uses `JPEGProcess1`, `JPEGProcess2_4`, `JPEGProcess14`, `MPEG4AVCH264HighProfileLevel41` — not the human-friendly aliases.

9. **`DicomAssociation` exposes `RemoteHost`/`RemotePort` only** (`FocusMedScp.cs:71,133`). There is **no** `IPEndPoint` on the association — do not write `association.RemoteEndPoint`.

10. **Reject with `DicomRejectReason.NoReasonGiven`** (`FocusMedScp.cs:83,142`). The enum has no `Normal` member. There are two reject paths: AE whitelist denial (line 83) and zero presentation-contexts accepted (line 142).

11. **Storage Commitment needs per-site config.** `DicomNetworking.StorageCommitmentScuMapping` must list every calling AE that expects N-EVENT-REPORT callbacks. Without a mapping, N-ACTION is accepted but the device never receives confirmation.

12. **MWL entry condition is non-standard** (`FocusMedScp.cs:259`). C-FIND routes to MWL when `QueryRetrieveLevel` is empty **or** `ScheduledProcedureStepSequence` is present — not the strict `QueryRetrieveLevel == "WORKLIST"` check. MWL patient-name search uses `EF.Functions.Like` (line 269), not raw SQL. `OnCFindRequestAsync` delegates to `ExecuteCFindQueryAsync`; the outer method adds try/catch returning `DicomStatus.ProcessingFailure`. Test with real devices before assuming a non-standards-compliant SCU is broken.

13. **`Program.cs` re-binds `DicomNetworking` config** (`Program.cs:44-50`) into a fresh `DicomNetworkingOptions` so it can set `DicomServiceOptions.MaxPDULength`, instead of injecting `IOptions<DicomNetworkingOptions>` from DI. Works today; will silently diverge if the options class ever adds validation.

14. **Print execution is decoupled.** N-ACTION marks `PrintJob.Status = Completed` immediately upon receipt. Physical printing is triggered by `PrintExecutionService.PrintAsync(...)` from the Dashboard. `PrintJob` has optional `PatientId`/`StudyId` nullable FKs — linked in N-SET after `IngestPrintImageAsync` creates the study, NOT in N-CREATE. N-CREATE always creates PrintJob with null FKs. `PrintExecutionService.ExecutePendingPrintJobAsync(id)` does **not** exist.

15. **`StorageCommitmentJob.Status` is an enum** (`StorageCommitmentStatus`), not a string. Values: `Pending=0`, `Completed=1`, `Failed=2`. Stored as `int` via `HasConversion<int>()`.

16. **Concurrent C-STORE race condition.** Multiple C-STORE requests for the same study use `ConcurrentDictionary<string, SemaphoreSlim>` per study UID in `DicomUpsertService` to serialize inserts. Duplicate studies are allowed — each C-STORE creates new Study/Series/DicomImage records.

17. **SQLite DateTime UTC.** SQLite stores dates as TEXT (ISO8601). `GetDicomDate()` returns `DateTime.SpecifyKind(date, DateTimeKind.Utc)`. Never use `DateTime.Now` or unspecified-kind DateTimes in entities.

18. **Association rejects when zero presentation contexts are accepted** (`FocusMedScp.cs:133-145`). After iterating all PCs, if `accepted == 0` the SCP sends `SendAssociationRejectAsync` with `DicomRejectResult.Permanent` / `DicomRejectReason.NoReasonGiven` and returns immediately — it never calls `SendAssociationAcceptAsync`. This prevents "successful" associations with no usable SOP classes.

19. **All three data-transfer handlers have try/catch for error resilience.** `OnCStoreRequestAsync` (line 200) returns `DicomStatus.ProcessingFailure` on exception. `OnCFindRequestAsync` (line 226) delegates to `ExecuteCFindQueryAsync` and returns `DicomStatus.ProcessingFailure` on failure. `OnCMoveRequestAsync` (line 397) wraps its DB query in try/catch and yields a failure response with `NumberOfFailedSuboperations=1`. None of these throw — they return DICOM error responses instead.

20. **N-SET, N-ACTION, and N-DELETE never throw exceptions to the DICOM framework.** All three construct their own `DicomDataset` command datasets (e.g. `DicomCommandField.NSetResponse`, line 710) and return explicit `DicomStatus.ProcessingFailure` responses from catch blocks. When building failure responses, they use the manual `DicomDataset` constructor with `CommandField`, `MessageIDBeingRespondedTo`, `Status`, and `CommandDataSetType` fields — not the request-based response constructor. This pattern prevents fo-dicom from logging unhandled exceptions on the network thread.

21. **`AssociationOutcome` has four values** (`AssociationAuditEntry.cs:15-21`): `Success=0`, `Rejected=1`, `Failed=2`, `PartiallyAccepted=3`. `PartiallyAccepted` is used at `FocusMedScp.cs:150` when at least one PC is accepted but at least one is also rejected. Do not assume an association is either fully accepted or fully rejected.

22. **Color vs. grayscale print is determined by `Association.PresentationContexts`** (`FocusMedScp.cs:603-607`), not by the request dataset alone. The SCP checks whether `BasicColorPrintManagementMeta` was accepted in any PC. As a fallback, `PrintPriority == "COLOR"` in the dataset also triggers color mode. If you change the PC negotiation list, verify that `isColor` still resolves correctly.

23. **N-DELETE eagerly loads the full delete cascade** (`FocusMedScp.cs:922-935`). The query uses `.Include(p => p.Patient).Include(p => p.FilmBoxes).ThenInclude(fb => fb.ImageBoxes).AsSplitQuery()` to load all child entities in separate SQL queries, then calls `RemoveRange` for each level. Do not remove the `Include` calls — doing so would cause N+1 queries or orphaned rows depending on cascade configuration.

24. **DICOM print SCU was removed** (`PrintScuService.cs`, `IPrintScuService.cs`). The class was dead code — never injected or called. `FocusMedScp` still handles N-CREATE/N-SET/N-ACTION/N-DELETE for DICOM Print Management on the SCP side. Dashboard printing uses the `FocusMed.Printing` engine (Windows driver / raw TCP).

25. **N-SET's missing-SOP-UID guard returns a manual failure response** (`FocusMedScp.cs:707-719`). Before entering the try/catch, `OnNSetRequestAsync` checks for an empty `sopUid` and returns a `DicomNSetResponse` built from a raw `DicomDataset` with `Status = InvalidArgumentValue`. This is distinct from the catch-block failure path (line 750) which uses `ProcessingFailure`. Do not consolidate these into one path — they communicate different DICOM error codes to the SCU.

26. **NEVER generate UNKNOWN patient names.** All fallback paths use empty string `""` — never `UNKNOWN_<GUID>`. `StoreFileOnlyAsync` (line 38), N-SET handler (line 760), and `PngExtractionService` (line 168) all use `""` as fallback. UNKNOWN pollutes the DB with phantom patient records that confuse the dashboard.

27. **N-SET patient resolution chain** (`FocusMedScp.cs:720-763`): (1) FK chain: `imageBox→FilmBox→PrintJob→Patient`, (2) inner DICOM dataset: `PatientID`/`PatientName` from `BasicColorImageSequence`/`BasicGrayscaleImageSequence`, (3) **top-level `request.Dataset`** `PatientID`/`PatientName`, (4) empty string. The former "most recent C-STORE study" fallback was **removed** — it risked cross-patient PHI contamination. Resolution is logged at Information with the source tag ("PrintJob"/"InnerDataset"/"TopLevelDataset"/"none") so identity flow is verifiable. Never generates UNKNOWN.

28. **N-DELETE removes PrintJob from DB** (`FocusMedScp.cs:948`). Previously only marked `Status = Completed`, leaving orphaned rows. Now calls `db.PrintJobs.Remove(printJob)` after removing child FilmBoxes and ImageBoxes.

29. **StorageCommitmentScuService AE titles** (`StorageCommitmentScuService.cs:91,140`). `DicomClientFactory.Create` parameters are `_ourAet, job.CallingAet` (we are SCU, remote is SCP). Previously swapped — would cause strict SCPs to reject the association.

30. **FilmBox N-CREATE fallback is association-scoped, not arbitrary** (`FocusMedScp.cs:565-605`). When `ReferencedFilmSessionSequence` is missing or the referenced PrintJob is not found, the SCP reuses (a) the association's `_fallbackPrintJobSopUid` (set from the first FilmBox N-CREATE of this connection), then (b) the most recent PrintJob from the **same calling AE within 60s**, else (c) **creates an implicit PrintJob** — never an orphaned FilmBox. Do NOT resurrect the old `OrderByDescending(CreatedAt)` global fallback (picked up unrelated PrintJobs) nor go back to orphaning FilmBoxes.

31. **N-ACTION with empty SOP UID does not complete arbitrary PrintJob** (`FocusMedScp.cs:875`). When both `sopUid` and `sopClassUid` are empty, logs a warning and returns Success without modifying any PrintJob. Previously fell back to completing the most recent PrintJob.

32. **N-DELETE with empty SOP UID returns `Success`** (`FocusMedScp.cs:955-965`). Some SCUs tear down without a SOP Instance UID; rejecting with `InvalidArgumentValue` broke their print flow ("already connected"-style device errors). The handler logs a warning and returns a manual `DicomNDeleteResponse` with `Status = Success`. Empty-UID N-DELETE is tolerated; non-empty UIDs still go through the full DeleteWithTransfer rollback path.

33. **N-SET returns `ProcessingFailure` when ImageBox not found** (`FocusMedScp.cs:786-794`). Previously returned `Success` even when the imageBox lookup failed, misleading the SCU.

34. **EF Core `MultipleCollectionIncludeWarning` — use `.AsSplitQuery()`.** Any query with 2+ collection navigations (e.g., `Include(Series).ThenInclude(Images)` + `Include(Frames)`) triggers this warning. Add `.AsSplitQuery()` to split into separate SQL queries. Fixed in `StudyCompletionService.cs:51`, `PngExtractionService.cs:51`, `FocusMedScp.cs:945`.

35. **PNG extraction is on-demand, not automatic.** PNGs are only generated when a viewer calls `GetOrExtractFramesAsync(studyId)`. C-STORE and study completion do NOT extract PNGs. This keeps the receive pipeline fast and avoids CPU spikes. PNGs persist on disk permanently once extracted.

36. **C-STORE now returns `ProcessingFailure` on DB/save errors.** `StoreFileOnlyAsync` (`DicomUpsertService.cs:164`) re-throws exceptions. `OnCStoreRequestAsync` catches them and returns `DicomStatus.ProcessingFailure`. Previously swallowed exceptions silently succeeded — the sender would never retry. Also cleans up orphaned `.dcm` files on failure.

37. **StorageCommitmentScuService only marks Completed if N-EVENT-REPORT was sent.** `SendNEventReportAsync` returns `bool` (`StorageCommitmentScuService.cs:68`). When no AET mapping exists, the job stays `Pending` and a warning is logged. Previously marked `Completed` even when the send silently returned.

38. **`PrintExecutionService` is registered in DI** (`Printing/DependencyInjection.cs:27`). It is `AddSingleton`. Do not remove this registration — Dashboard + print flow resolve it.

39. **PngExtractionService `_studyLocks`/`_studyRefCount` cleanup** (`PngExtractionService.cs:211`). When refcount reaches 0, both the semaphore and refcount entry are removed from their static dictionaries. Previously `_studyLocks.TryRemove` was called immediately after `Release()`, allowing another thread to acquire a semaphore that was about to be deleted. Now removal is conditional on `remaining <= 0`. Refcount is only managed by `GetOrExtractFramesAsync` (increment) and `ReleaseStudyPng` (decrement) — `ExtractForImageAsync` does NOT touch refcount.

40. **DicomUpsertService `_studyLocks` ref-counted cleanup** (`DicomUpsertService.cs:18-19`). Uses `ConcurrentDictionary<string, int> _studyLockCounts` alongside `_studyLocks`. Each `WaitAsync` increments the count, each `Release` decrements. When count reaches 0, both dictionaries remove the entry. Prevents race condition where `TryRemove` deletes a semaphore another thread is about to acquire.

41. **StorageForwardQueue `PendingCount` only increments on successful enqueue** (`StorageForwardQueue.cs:26`). `TryWrite` return value is checked before `Interlocked.Inrement`. Previously inflated the counter even when the channel was completed/full.

42. **Data directory resolution uses `FOCUSMED_DATA` env var directly** (`DicomUpsertService.cs:24`, `PngExtractionService.cs:33`). Both services read `Environment.GetEnvironmentVariable("FOCUSMED_DATA")` with fallback to `%LOCALAPPDATA%\FocusMed`. Previously read a non-existent `DataDirectory` config key. `IConfiguration` parameter removed from `DicomUpsertService` constructor.

43. **StudyCompletionService re-checks image count before marking Complete** (`StudyCompletionService.cs:60`). After querying ready studies, re-counts images per study. If the count changed (C-STORE arrived during the query), the study's `LastUpdatedAt` is bumped and completion is deferred. Prevents marking a study Complete while images are still arriving.

44. **N-DELETE returns `ProcessingFailure` when PrintJob not found** (`FocusMedScp.cs:947`). Previously returned `Success` even when the SOP UID matched no PrintJob, misleading the SCU.

45. **`DicomHelpers.SanitizeFileName` returns `""` not `"UNKNOWN"`** (`DicomHelpers.cs:5`). Also strips `\` and `/` characters to prevent path traversal. Empty input returns empty string per AGENTS.md #26.

46. **`DicomHelpers.GetDicomDate` trims whitespace and uses `CultureInfo.InvariantCulture`** (`DicomHelpers.cs:26`). Locale-dependent parsing was a latent bug. Returns `null` for empty/whitespace-only input.

47. **Duplicate C-STORE after Complete creates new study with shared UID** (`DicomUpsertService.cs:96-108`). When SOP UID exists in a completed/archived study, a new study UID is generated. `_newStudyAfterCompleteCache` ensures all C-STORE images within 5 min for same patient+date share the same new UID (prevents 1-image-per-study fragmentation).

48. **Reverse merge captures PRINT directory BEFORE moving files** (`DicomUpsertService.cs:671-673`). The PRINT study directory path must be captured before the move loop updates `img.FilePath`. Otherwise the cleanup deletes the C-STORE study directory.

49. **N-DELETE handles concurrent delete from merge** (`FocusMedScp.cs:957-965`). Catches `DbUpdateConcurrencyException` when reverse merge already deleted the PrintJob. Returns Success instead of ProcessingFailure.

50. **`DicomHelpers.FormatPatientName` is the shared helper** (`DicomHelpers.cs:53`). Replaces 4 duplicate private methods across Home/Archives/DeletedStudies/StudyDetails. Returns `"Inconnu"` for null/whitespace; replaces `^` with space.

51. **PdfService cover generation uses Word COM, not MiniPdf** (`PdfService.cs:303-387`). A4 covers: copy `cover.docx` to temp, Word COM `doc.SaveAs2(..., FileFormat: 17)` with `{{PatientName}}`/`{{StudyDate}}` replaced via `Find.Execute(Replace: 2)`. A3 covers always use the QuestPDF fallback (A4 docx would not scale). If `cover.docx` is missing from the data dir, the system silently falls back to QuestPDF. Dashboard startup auto-copies `wwwroot/cover.docx` and `wwwroot/cover-logo.jpg` into the data dir when missing. `MiniPdf` package has been removed from all csproj files.

52. **PngExtractionService refcount is only in `GetOrExtractFramesAsync` and `ReleaseStudyPng`.** `ExtractForImageAsync` (`PngExtractionService.cs:144`) acquires a per-study semaphore for thread safety but does NOT touch `_studyRefCount`. Do NOT add refcount management to `ExtractForImageAsync` — it would cause double-decrement since the caller already manages refcount.

53. **`DeletedStudies.razor` must declare `@implements IDisposable`** (`DeletedStudies.razor:3`). It creates a `Timer` instance in `OnInitializedAsync`. Without `IDisposable`, the timer leaks on navigation (Blazor disposes the component but the timer's CLR reference keeps firing, causing `ObjectDisposedException`). `Archives.razor` does **not** have a Timer and does **not** implement `IDisposable`.

54. **`StudyDetails.razor` has no `@using FellowOakDicom`** — removed as dead code. `NavigationManager` was absent too, but is now injected as `Nav` for the **manual merge** post-redirect (`ConfirmMerge` → `Nav.NavigateTo($"/study/{targetId}", forceLoad: true)`). All other navigation still uses `<a href>` links.

55. **Data layer has 4 enums.** `StorageCommitmentStatus` (Pending/Completed/Failed), `PrintStatus` (Pending/Printing/Completed/Failed), `StudyStatus` (Receiving/Complete/Failed/Archived/Deleted), `AssociationOutcome` (Success/Rejected/Failed/PartiallyAccepted). `PrinterType` (GrayLevel/Multicolor) is in `FocusMed.Dicom.Options`, not the Data layer.

56. **`BackfillMetadataAsync` runs on startup** (`Worker/Program.cs:98` → `MetadataBackfillService`). On first boot or after adding the metadata migration, `DicomUpsertService.BackfillMetadataAsync()` iterates all DICOM images in batches of 200 and backfills missing metadata (BirthDate, Sex, Description, AccessionNumber, InstitutionName, Manufacturer, ReferringPhysicianName) from the `.dcm` files into the database. This is a one-time operation per image. The hosted service passes its cancellation token and covers all 7 metadata fields in the WHERE filter.

57. **`StudyStabilizationSeconds` config key** (`appsettings.json:34`, default 60). Controls how long since `LastUpdatedAt` before a study is considered stable and marked Complete. `StudyCompletionService` polls every 5 seconds and marks studies as Complete when `LastUpdatedAt <= UtcNow - stabilizationSeconds` and no new images arrive. `OperationCanceledException` is filtered from the error log so shutdown is quiet.

58. **`FocusMedDbContextFactory`** (`FocusMedDbContextFactory.cs`) is the `IDesignTimeDbContextFactory` for EF migrations. Reads `FOCUSMED_DB_CONNECTION` env var with fallback to `Data Source=focusmed.db`. Not registered in DI — only used by `dotnet ef` tooling.

59. **Dashboard `/pdf-cache` serves with `ServeUnknownFileTypes = true`** (`Program.cs:61-67`). This is intentional — PDFs are served by filename only (no extension in URL path). The `DefaultContentType` is `application/pdf`. Do not change `ServeUnknownFileTypes` to `false` or PDFs won't load in the iframe.

60. **`_mergeLocks` is a separate static dictionary from `_studyLocks`** (`DicomUpsertService.cs:18`). `_studyLocks` serializes per-study C-STORE inserts. `_mergeLocks` serializes forward/reverse merge operations. They use the same `ConcurrentDictionary<string, SemaphoreSlim>` pattern but must not be consolidated — merge and insert are independent operations that can safely overlap.

61. **`DicomImage.Source` defaults to `"C-STORE"`** (`DicomImage.cs:10`). Populated by `DicomUpsertService` — value is `"C-STORE"` for ingested images. Used to distinguish image origin.

62. **PrintCapture uses Local Port, not PORTPROMPT:** (`PrinterSetupService.cs`, `appsettings.json`). The "FocusMed" printer uses `Microsoft Print To PDF` driver with `Local Port` named `C:\FocusMed_Prints\incoming.pdf`. The Local Port writes PDF output directly to that file with no dialog. `PrintJobMonitorService` uses a **200ms `Timer` poller** (NOT `FileSystemWatcher` — spooler writes via `localspl.dll` which `FileSystemWatcher` can't see). Polls `incoming.pdf` every 200ms, waits 300ms for size stabilization, then copies to `resumes/` and fires the popup. Total detection: ~500ms. Do NOT change the port back to `PORTPROMPT:` — that shows a Windows save dialog.

62b. **PrintCapture popup lists only TODAY's unassigned studies.** (`DatabaseService.GetSelectableStudiesAsync` — `resumes\DatabaseService.cs:27-43` and `ResumePickerWindow.xaml.cs`). The popup shows only studies created **today** (local-time "today" converted to UTC, compared against `CreatedAt`) AND with **no resume** (`ResumePdfPath` null/empty), so a fresh print shows a short, fast list of the day's studies still needing a resume. Already-assigned studies never appear (no replace dialog, no violet badge). The "Statut"/Resume badge column was removed from `ResumePickerWindow.xaml` and the `HasResume` property dead-code swept from the code-behind. `AssignResumeAsync` still deletes the old file + overwrites if a resume somehow already exists.

63. **Launcher process lock issue.** `FocusMed.Launcher.exe` can be locked by running processes in other user sessions. Build fails with MSB3027 when the exe is in use. Workaround: rename the locked exe before building, or kill the process first. `Stop-Process -Name "FocusMed.Launcher" -Force` may fail with access denied if the process is in another session.

64. **Unlink Resume button on StudyDetails** (`StudyDetails.razor:89-102`). The violet `Résumé` badge shows only "Résumé associé" — the long `resume_<id>_<guid>.pdf` filename is NOT shown in the pill (it moved to the element `title` tooltip for reference). A red pill button "Retirer" appears next to it when `Study.ResumePdfPath != null`. Click opens a `ConfirmModal` asking "Le fichier PDF du resume sera supprime definitivement et ne pourra plus etre reassocie a cette etude. Continuer ?". On confirm, `StudyService.UnlinkResumeAsync(Study.Id)` (`StudyService.cs:116-148`) deletes the PDF from `%LOCALAPPDATA%\FocusMed\{study.ResumePdfPath}` AND clears `Study.ResumePdfPath` (file delete is wrapped in try/catch — DB update always succeeds even if the file is already gone). Uses `ToastNotification` for success/error feedback. After unlink, triggers `RegeneratePdfPreviewAsync()` to refresh the iframe without the resume page.

65. **Toast auto-dismiss uses a sequence guard** (`Home.razor:516`, `Archives.razor:343`, `DeletedStudies.razor:307`, `StudyDetails.razor:627`). Every page's `ShowToast` increments `_toastSeq`, waits 3s, and only clears the message if the seq still matches. This prevents a fast second toast from being erased by a delayed first toast's timer.

66. **Booklet print pipeline** (`PrintExecutionService.cs`, `BookletImpositionService.cs`). The Dashboard generates an A4 PDF (cover + optional resume + 1 image/page), pads it to a multiple of 4 pages, then `PrintExecutionService` runs our own `BookletImpositionService` A4→A3 landscape 2-up. The imposed A3 landscape PDF is sent to the Windows driver with `PageMediaSize=ISOA3`, `Stapling=SaddleStitch`, duplex short-edge, and Konica-specific XML injection setting `CStapleFold=On` and `Folding=On`. We never set `Booklet=On` — that confused the Konica driver on size mismatch.

67. **Home.razor toast has a sequence guard** (`Home.razor:516`). It was the only page missing `_toastSeq`; now matches Archives/DeletedStudies/StudyDetails.

68. **StudyDetails preview page size matches the selected print mode** (`StudyDetails.razor:521-522`). FlatA3 regenerates the preview at A3; Booklet/FlatA4 use A4. The actual A3 booklet imposition only happens at print time.

69. **MergePdfs padding is opt-in** (`PdfService.cs:499`). `padToMultipleOf4` is only true for booklet generation. Preview and flat-A3/A4 prints no longer pad with blank pages.

70. **PrintCapture cleans up the monitor temp PDF** (`ResumePickerWindow.xaml.cs`). The monitor copies `incoming.pdf` → `resumes/{timestamp}_{guid}.pdf`. The picker copies that temp → `resumes/resume_{id}_{guid}.pdf`. The picker now deletes the monitor temp on window close (success, cancel, or user clicking X). Previously every print left an orphan.

71. **PrintJobMonitorService validates PDF completeness** (`PrintJobMonitorService.cs:213`). Checks `%PDF` header **and** `%%EOF` trailer before copying. Prevents truncated PDFs from spooler bursts.

72. **A3 auto-detect uses exact millimeter match** (`PrintExecutionService.cs:126-130`). Compares against A3 (297×420 / 420×297) with 2mm tolerance. Letter/Legal no longer misclassify as A3.

73. **StorageForwardService opens the .dcm per-target** (`StorageForwardService.cs:75-98`). `DicomFile.OpenAsync` happens inside each target's try/catch, so a concurrently-deleted file cannot fault the whole host. The shutdown log honestly states queued forwards are not re-sent.

74. **MetadataBackfillService passes cancellation + covers all 7 metadata fields** (`MetadataBackfillService.cs:23`, `DicomUpsertService.cs:256-339`). WHERE filter includes BirthDate, Sex, Description, AccessionNumber, InstitutionName, Manufacturer, ReferringPhysicianName. Shutdown is prompt.

75. **StudyCompletionService does not log normal shutdown** (`StudyCompletionService.cs:26`). `OperationCanceledException` is filtered from the `catch` so `StopAsync` noise is gone.

76. **`DicomHelpers` is public** (`DicomHelpers.cs:1`). `SanitizeFileName`, `GetFnv1aHash`, `GetDicomDate`, `FormatPatientName` are shared across projects.

77. **Settings page removed.** `Settings.razor` was deleted (2026-08-29). The `IPrinterSettingsStore`/`PrinterSettingsStore`/`PrintSettings` and its `LoadAsync` wiring in StudyDetails were removed with it — StudyDetails hardcodes Konica presets and `Copies=1`. `printer-settings.json` is no longer written or read. Do not resurrect the settings store.

78. **`MiniPdf` package removed** from `FocusMed.Dashboard.csproj` and `FocusMed.Printing.TestConsole.csproj`. Cover generation uses Word COM (A4) or QuestPDF fallback (A3).

79. **`PrintScuService` and `IPrintScuService` removed** (`FocusMed.Dicom`). Dead code — never injected or called. Dashboard printing uses `FocusMed.Printing` engine exclusively.

80. **`FocusMed.Printing` project location** (`src/FocusMed.Printing/`). `PrintExecutionService` lives here, **not** in `FocusMed.Dicom`. AGENTS.md previously listed it under Dicom — that was wrong.

**Warning:** The agent gotcha list intentionally grows. When it exceeds ~90 entries, migrate stable facts into `README.md`.

81. **Post-Settings dead-code sweep** (2026-08-29). Removed with `Settings.razor`/`Prints.razor`: `PrintAuditEntry` entity + DbSet + migration (table dropped from DB + history row deleted), `FilmPrinterConfig`/`PrinterType`/`DicomNetworkingOptions.FilmPrinters` + Worker startup logging + `"FilmPrinters"` config key (DICOM Print SCU was already dead), `ITestPageService`/`TestPageService`, `IPrinterSettingsStore`/`PrinterSettingsStore`/`PrintSettings`, dead `RawPrinterPreset` fields (`IsBooklet`/`ForceGrayscale`/`Copies`). Nav bar now has exactly **3** links: Études / Archives / Supprimées. `Prints.razor`/`Settings.razor` do not exist — do not reference `/prints` or `/settings`.

82. **Layout toolbar on StudyDetails** (`StudyDetails.razor`). Print + layout controls live in a second card row organized as **3 titled `grid` zones** (stacked below the `xl` breakpoint): **Résumé** (status badge + "Retirer"), **Mise en page** (Images/page + Marge + Espacement mm), **Impression** (mode select + "Imprimer" gradient button, `justify-end` in a `bg-brand-50/50` panel so the primary action always sits right). The `Copies` field was **removed** — `PrintAsync` always gets `Copies = 1`. images/page is a FREE NUMBER TEXT INPUT (`ImagesPerPageInput`, **default is modality-dependent** via `DefaultImagesPerPage`): CT/OT studies default to `"1"` (one image per page for clinical review), other modalities (SC prints/prescriptions) default to `""` = Auto; empty = Auto, so any count (2, 3, 4, 5…) is auto-scaled to fit the page. Auto = per-page cap ≤ 6 with BALANCED page split done by `PdfService`: 13 → 5+4+4, 7 → 4+3 — every page fills. Manual numbers 1..16 override the default. Changed via `@bind:after="OnLayoutChanged"` → `RegeneratePdfPreviewAsync()` (debounced 150ms). Values feed `EffectiveImagesPerPage/GapPx/MarginPx` into `PdfService.GeneratePrintPdf/GenerateBookletPrintPdf/GenerateFlatPrintPdf`. Frame default selection: print frames (`Modality "OT"` for legacy, `"SC"` for new prints) are selected by default when a study mixes prints with C-STORE data — in an OT/CT merged study the films start checked and the CT frames are unchecked (user re-checks via Tout/Aucun). A study with only C-STORE frames (no print-like modality) still starts fully selected. The images/page Auto badge shows `Auto → N/page · M page(s)` live. StudyDetails breadcrumb is a single row (Études `/` patient `·` date) — the "Retour aux études" button was removed (breadcrumb + nav cover it). Sidebar thumbnails are small `w-28 h-24` with `object-contain` (never cropped), rows `py-1.5` `space-y-1`, and a `max-h-[420px] lg:max-h-none` scroll cap when the two-panel stacks.

83. **`PdfService.ComputeGridCols` is the shared aspect-aware grid helper** (`PdfService.cs`). Replaces the old hardcoded switch. It probes PNG IHDR width/height (`EstimateImageAspect`, first 8 files, median) to pick the grid ratio that best fills the page without cropping. Cost = `|ln(rows/cols) − ln(imageAspect/pageAspect)| + 0.35·(wasted cells/count)` — rows = `ceil(count/cols)`. The grid is recomputed **PER PAGE BATCH** (balanced split) so the last partial page re-lays out and fills the whole page; the `imagesLayerCache` key is prefixed `images_v2` (busted when the balanced-split algorithm changed). Every cell gets an identical explicit height (`cellH`) so all images render at the same size (contain via `.FitArea()`, never cropped); image bytes are pre-read in parallel into a `ConcurrentDictionary`. Do not inline a second grid switch elsewhere.

84. **Images-layer PDF cache** (`PdfService.cs:444-458`). `GenerateImagesPdf` caches its output as `pdf-cache/images_{md5}.pdf` keyed on `(pageSize|perPage|gap|margin|paths)`. Cover PDF (SHA256, 24h TTL) + images layer (60min TTL) + merged PDF (60min) are cached independently — frame toggles only re-render the changed images layer in QuestPDF, not the whole merge. `CleanupOldPdfsAsync` sweeps all three (separate dirs for covers).

85. **XPS page visual must NOT be a `FixedPage`** (`PrintExecutionService.cs:213`). `PdfPagePaginator.GetPage` returns a `DocumentPage` whose root visual is a **`Canvas`**, never a `FixedPage` — the XPS writer wraps each page in a `FixedPage` itself, and a nested `FixedPage` throws `XpsSerializationException: FixedPage cannot contain another FixedPage` (seen as "Windows print failed" in the log). The Canvas holds one `System.Windows.Controls.Image` (300-DPI PDFtoImage render, `Stretch.Fill`, frozen `BitmapImage`). If you change the visual container, ensure it is a plain layout root (`Canvas`/`Grid`), not a `FixedPage`/`FixedDocument`. Then rebuild + rerun; killed locked Dashboard exe before build per gotcha 6/63.

86. **SCP instance state is per-association** (`FocusMedScp.cs:98` `_fallbackPrintJobSopUid`). fo-dicom instantiates `FocusMedScp` once per association, so instance fields are safe for connection-scoped fallback state. Never convert these to static/shared fields — that would leak one connection's implicit PrintJob into another's.

87. **Auto-merge consolidates ALL modalities for the same patient into ONE active study** (`DicomUpsertService` + `StudyCompletionService`). Design rule: CT/OT/SC with the same `PatientId` always land in one study — there is **no time window on patient-based matches**. Merge resolution for prints (`IngestPrintImageAsync` → `ResolvePrintMergeTargetAsync`): (1) explicit `StudyInstanceUID` in the dataset matching any existing Study → merge there; (2) non-empty `patientId` with a `Receiving`/`Complete` study for that patient → merge (NO window); (3) empty patient + same `CallingAeTitle` within `PrintMergeWindowSeconds` (fallback grace, default **300**) → merge; (4) last resort: ANY active study within the grace window. The C-STORE path (`StoreFileOnlyAsync`) has the same consolidation: after the 15s same-UID dedup, it resolves the patient's most recent active study (`ResolveStoreTargetUidAsync`, pre-lock) and reuses it for ANY incoming modality — so a second incomplete CT or a CT+OT for the same patient merge instead of creating parallel studies. The per-study lock is keyed on the resolved target UID to serialize concurrent inserts. On merge, images/folders go into the **target study's existing archive directory** (via `Directory.GetParent` from the first image's `FilePath`), never a parallel `_CT_`/`_SC_` folder; the source Study row is removed (series re-pointed first, never deleted). UIDs are sanitized via `TruncateUid` (digits + dots only, max 64 chars). New print Series are tagged `Modality = "SC"`. **Reverse merge** (`StudyCompletionService.MergeRecentPrintsIntoStudyAsync` on study completion) absorbs `Receiving` print studies with images `Source == "PRINT"` (within grace window) PLUS **all same-patient studies regardless of timing** — it never absorbs unrelated C-STORE studies of a different patient. `MergeRecentAnonymousStudiesAsync` (called after a new C-STORE study is created) now merges **named prints too** (`PatientId == target.Patient.PatientId` OR empty `""`), not just anonymous. Both merge paths delete the orphaned `Patient` row once no other study references it. Self-tested: CT→OT→SC for P001 = 1 study with series [CT, OT, SC]; different patient stays separate.

88. **`CallingAeTitle` is recorded on ingest.** `Study.CallingAeTitle` (`Study.cs:14`) and `PrintJob.CallingAeTitle` (`PrintJob.cs:10`) are nullable strings set from `Association.CallingAE` — C-STORE (`StoreFileOnlyAsync`) and print N-CREATE/N-SET. New columns indexed (`Study.CallingAeTitle`, composite `PrintJob.{CallingAeTitle,CreatedAt}`) in migration `PrintMergeSupport`. Used by the auto-merge (gotcha 87) and the FilmBox fallback (gotcha 30). Never empty-string — null when unknown.

89. **Manual merge is a Dashboard feature: `StudyService.MergeStudyAsync(source,target)`** (`StudyService.cs:18-95`). Re-points `Series.StudyId` + `PrintJob.StudyId`/`PatientId` to the target, moves the source archive directory under the target's (best-effort, try/catch — DB stays consistent if the move fails), deletes **only** the source Study row (never `RemoveRange` on the re-pointed series — that cascades and deletes the just-moved images), and bumps the target's `LastUpdatedAt` + restores status from Archived/Deleted to Complete. UI: "Fusionner" button on `StudyDetails.razor` → picker modal lists non-deleted studies with image counts → ConfirmModal → `Nav.NavigateTo($"/study/{targetId}", forceLoad: true)`. Do NOT merge a study into itself (returns false).

90. **Tailwind CSS is compiled MANUALLY, not by `dotnet build`.** `npm run build:css` (in `src/FocusMed.Dashboard`, `tailwindcss -i ./tailwind.input.css -o ./wwwroot/app.css --content "./Components/**/*.razor"`) generates `wwwroot/app.css` from utility classes scanned in `.razor` files. If you add a Tailwind class never used before (e.g. `sticky top-0`, `min-h-0`), it will render as a **no-op `static`** until you rerun `npm run build:css`. After any CSS-class change: rebuild CSS, then rebuild the project (0/0), then restart Dashboard.

92. **localhost:5000 buttons dead, IP:5000 works — IPv4/IPv6 mismatch.** `Program.cs` calls `builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(webPort))` to bind a dual-stack socket (IPv4+IPv6) on port 5000. The Launcher's `ChildProcessSupervisor` additionally sets `ASPNETCORE_URLS=http://0.0.0.0:{port};http://[::]:{port}` (which takes precedence over the code binding when launched by the Launcher). Without dual-stack binding, `0.0.0.0` binds IPv4-only; Chrome resolves `localhost` to `::1` (IPv6) so the Blazor Server SignalR circuit never connects and clickable elements do nothing. The Launcher opens the dashboard via the auto-detected `LocalIp` (falling back to `127.0.0.1`) in `App.xaml.cs` (both single-instance relaunch and `OpenDashboard`) to avoid the dual-stack issue entirely. Symptom on client PCs: page loads, but every button is inert.

91. **Uniform page layout pattern (all 4 pages).** Every page root is `<div class="flex flex-col flex-1 min-w-0 gap-5">`; block spacing uses the parent `gap` (no `mb-6`/`mb-4`). List pages (Home/Archives/DeletedStudies) use the flexible table pattern: table card `flex-1 min-h-0 flex flex-col` + inner `<div class="overflow-auto flex-1 min-h-0">` + `<thead class="sticky top-0 z-10 bg-slate-50/50 ...">` + footer `shrink-0`. Section: on tall viewports the card grows to fill the screen (flex-1), on short ones the `<main>` scrolls. StudyDetails two-panel area is `flex flex-col lg:flex-row flex-1 min-h-[650px] gap-5` (stacks vertically below the `lg` breakpoint; sidebar `w-full lg:w-96` with the thumbnail list capped at `max-h-[420px] lg:max-h-none` when stacked; preview is the flex-1 partner). Responsive paddings: table cells/th/footers use `px-4 sm:px-6 py-4` / `px-4 sm:px-6 py-3.5`, toolbar/filter bars and the pagination footer use `flex-wrap`. Header is `min-h-16 py-2` (not fixed `h-16`) so it wraps without clipping. Card radius `rounded-xl`, inputs `rounded-lg border-slate-200`, badges ID/modality `px-2.5 py-1`. Keep this pattern when touching any page.

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
- `src/FocusMed.Dicom/StorageForwardService.cs` — hosted C-STORE SCU, per-target file open, no restart recovery.
- `src/FocusMed.Dicom/StorageForwardQueue.cs` — `Channel<T>`-based forward queue with `Complete()`/`PendingCount` for graceful shutdown.
- `src/FocusMed.Dicom/Options/DicomNetworkingOptions.cs` — `DicomNetworking` section binding + `StorageForwardTargets`.
- `src/FocusMed.Dicom/DicomHelpers.cs` — Static helpers: SanitizeFileName, GetFnv1aHash, GetDicomDate, FormatPatientName.
- `src/FocusMed.Data/FocusMedDbContext.cs` — 11 DbSets, fluent FK config, enum conversion, 22 indexes.
- `src/FocusMed.Data/FocusMedDbContextFactory.cs` — `IDesignTimeDbContextFactory` for EF migrations.
- `src/FocusMed.Data/DependencyInjection.cs` — `AddFocusMedData` extension method, registers `FocusMedDbContext` with Sqlite + `SqlitePragmaInterceptor` (WAL + busy_timeout + synchronous=NORMAL).
- `src/FocusMed.Data/Entities/StorageCommitmentStatus.cs` — `Pending=0`, `Completed=1`, `Failed=2`.
- `src/FocusMed.Data/Migrations/` — 2 EF migrations (latest: `PrintMergeSupport`).
- `src/FocusMed.Printing/Jobs/PrintExecutionService.cs` — print execution (Windows driver XPS path + raw TCP fallback), booklet imposition, Konica finishing injection, streaming paginator, A3 exact-detect.
- `src/FocusMed.Printing/Imposition/BookletImpositionService.cs` — A4→A3 landscape 2-up imposition (4-slot signature math).
- `src/FocusMed.Printing/Options/RawPrinterConfig.cs` — `RawPrinters` section, presets (Name/Ip/Port/PaperSize/WindowsPrinterName/Copies).
- `src/FocusMed.Dashboard/Program.cs` — Blazor Server entry, registers services, serves `/images` and `/pdf-cache` from data dir, auto-provisions cover assets from wwwroot, migration.
- `src/FocusMed.Dashboard/Components/Pages/Home.razor` — studies list, search, date filter, pagination, delete modal, sortable columns.
- `src/FocusMed.Dashboard/Components/Pages/StudyDetails.razor` — patient info, study metadata, PNG image grid viewer, print dropdown (BookletA3/FlatA3/FlatA4), lightbox.
- `src/FocusMed.Dashboard/Components/Pages/Archives.razor` — `/archives` route, archived studies with search/filters/pagination.
- `src/FocusMed.Dashboard/Components/Pages/DeletedStudies.razor` — `/deleted` route, deleted studies with restore/permanent delete, 10s auto-refresh timer.
- `src/FocusMed.Dashboard/Components/Layout/MainLayout.razor` — nav bar (Études, Archives, Supprimées), server status badge (TCP liveness probe).
- `src/FocusMed.Dashboard/Components/Shared/ToastNotification.razor` — toast notification component (role=alert, aria-live).
- `src/FocusMed.Dashboard/Components/Shared/ConfirmModal.razor` — confirmation modal with French defaults, focus on open + Escape key, double-click guard.
- `src/FocusMed.Dashboard/Services/PdfService.cs` — PDF generation: cover (Word COM A4 / QuestPDF A3 fallback), images (QuestPDF), merge (PdfSharpCore), cache (MD5 hash + 60min TTL), cover cache (SHA256 + 24h TTL). `padToMultipleOf4` only for booklet.
- `src/FocusMed.Dashboard/Services/StudyService.cs` — shared delete/soft-delete/restore/archive/unlink-resume logic. Registered as Scoped.
- `src/FocusMed.Dashboard/Services/DeletedCleanupService.cs` — BackgroundService, auto-deletes Deleted studies after 30 days.
- `src/FocusMed.Launcher/FocusMed.Launcher.csproj` — WPF supervisor app (.NET 10-windows), references FocusMed.Data, `UseWindowsForms=true` for tray icon. Absorbed the former `FocusMed.PrintCapture`.
- `src/FocusMed.Launcher/icon.ico` — Embedded resource, 16x16 blue "F" tray icon.
- `src/FocusMed.Launcher/App.xaml.cs` — WPF entry point, DI setup, single-instance guard, startup of children, tray icon. **System tray icon** (NotifyIcon): right-click menu (Ouvrir le Dashboard / Quitter), double-click opens `http://localhost:5000`, balloon notifications on startup and print capture. Starts hidden, window appears on print only.
- `src/FocusMed.Launcher/Services/SiteConfig.cs` — never-fail `config.json` loader (typed defaults, corrupt-file backup).
- `src/FocusMed.Launcher/Services/ChildProcessSupervisor.cs` — watchdog restarting Worker + Dashboard children (restart policy + backoff).
- `src/FocusMed.Launcher/Services/BootstrapService.cs` — one-time setup: folders, autostart task, firewall rule, virtual printer, DB migration. Every step tolerated.
- `src/FocusMed.Launcher/Services/PrinterSetupService.cs` — creates "FocusMed" virtual printer on Windows using Local Port `C:\FocusMed_Prints\incoming.pdf` (no dialog — PDF auto-saved).
- `src/FocusMed.Launcher/Services/PrintJobMonitorService.cs` — Timer poller on `incoming.pdf` (every 200ms), waits 300ms for size stabilization, validates `%PDF` + `%%EOF`, copies to `resumes/` folder, fires popup. Total detection: ~500ms.
- `src/FocusMed.Launcher/Services/DatabaseService.cs` — EF Core queries for selectable studies + resume assignment.
- `src/FocusMed.Launcher/Windows/ResumePickerWindow.xaml` — WPF popup for study selection.
- `src/FocusMed.Launcher/Windows/ResumePickerWindow.xaml.cs` — code-behind for picker window, deletes monitor temp PDF on close, uses configured `ResumesFolder`.

## Launcher (single-EXE deployment)

Located `src/FocusMed.Launcher/` (`net10.0-windows`, WPF + WindowsForms). Absorbed the former `FocusMed.PrintCapture`. Not built/published in this worktree � source only.

**What it supervises:** starts `FocusMed.Worker.exe` (DICOM listener 11112) and `FocusMed.Dashboard.exe` (Blazor :5000) as hidden child processes (CWD = folder containing each exe, so their `appsettings.json` resolves), exported env `FOCUSMED_DATA` + `FOCUSMED_DB_CONNECTION` for both. `ChildProcessSupervisor` always restarts a child on exit with backoff (no backoff on first start; 2s restart; after 3 fast exits <5s uptime, wait 10s?30s cap; backoff resets on a healthy long run). One child failing never takes down the launcher.

**config.json** (in the launcher's exe dir, loaded by `SiteConfig.Load`; never-fail � corrupt files are backed up to `config.errored-{ts}.json`): `AETitle`, `DicomPort` (11112), `WebPort` (5000), `DataDirectory` (empty = `%LOCALAPPDATA%\FocusMed`), `RawPrinterIp` (192.168.1.160), `RawPrinterPort` (9100), `KonicaWindowsPrinterName`, `VirtualPrinterName` (FocusMed), `OutputDriverName` (Microsoft Print To PDF), `PrintJobsFolder` (C:\FocusMed_Prints), `ResumesFolder` (resumes), `AutostartEnabled` (true), `AutoOpenDashboardOnStart` (false).

**Autostart task:** `schtasks /Create /TN "FocusMed" /TR "\"{launcherExe}\" --autostart" /SC ONLOGON /RL HIGHEST /F` (only when `AutostartEnabled`). `--autostart` suppresses the startup balloon. **Firewall rule:** `FocusMed DICOM TCP 11112` inbound allow TCP on `DicomPort`. **Virtual printer:** "FocusMed" via `Microsoft Print To PDF` driver on Local Port `C:\FocusMed_Prints\incoming.pdf`.

**Env vars exported by the launcher:** `FOCUSMED_DATA` ? resolved data dir, `FOCUSMED_DB_CONNECTION` ? `Data Source={dataDir}\focusmed.db`, set before any DI. For the Dashboard child, `ASPNETCORE_URLS=http://0.0.0.0:{WebPort}` (LAN-reachable UI). Worker + Dashboard `appsettings.json` no longer hardcode `ConnectionString` � they fall back to `FOCUSMED_DB_CONNECTION`, so the launcher's value wins. Single-instance via mutex `Local\FocusMed.Launcher`.

**Shared appsettings.json:** publish puts 3 exes in one folder, so their 3 `appsettings.json` files would clobber each other. `BootstrapService.EnsureSharedAppSettings()` (public, called synchronously in `App.xaml.cs` BEFORE any child starts, and re-asserted inside `BootstrapAsync`) regenerates ONE merged `appsettings.json` in the exe dir from `config.json` defaults: Serilog (file sinks to `%FOCUSMED_DATA%/logs`), `StudyStabilizationSeconds`, `PngExtraction`, `DicomNetworking` (AE/DicomPort from config.json, no storage-forward targets), `RawPrinters` (3 Konica presets built from `RawPrinterIp`/`RawPrinterPort`/`KonicaWindowsPrinterName`). Children read it from their CWD. Worker binds `0.0.0.0` from `DicomNetworking:BindAddress`; Dashboard binds `ASPNETCORE_URLS` exported by the launcher. Corrupt-file backup/regenerate logic lives in `SiteConfig.Load`.

**Restart policy:** always restart; crash-loop detection per child with `<5s` uptime ? `fastExits++`, =3 ? backoff. Missing child exe is logged (throttled to 30s) instead of spamming Critical every 500ms. Tray tooltip (updated every 3s, `NotifyIcon.Text` max 63 chars) shows `FocusMed: Worker {Running|Stopped|Missing} | Dashboard {...}`.

**Note:** `deploy/` is the live single-folder deployment (Worker + Dashboard + Launcher + merged `appsettings.json` + `config.json`) — republished and E2E smoke-tested (Worker binds 11112, Dashboard serves :5000 `/health` ok, migrations + metadata backfill ran). To republish after code changes (preserve merged appsettings + config.json): back up `deploy/appsettings.json`, then `dotnet publish` the Worker, Dashboard, Launcher csproj `-c Release -o deploy` in that order, then restore `appsettings.json` (the launcher regenerates it from `config.json` on startup anyway). Run the whole flow via `& "D:\FocusMed\deploy\FocusMed.Launcher.exe"` in an **Administrator** PowerShell — it supervises Worker + Dashboard and runs the resume print monitor. Do NOT run dev Worker/Dashboard (`dotnet run`) at the same time as the launcher (port 11112/5000 conflict + double restart supervision). `deploy/` is gitignored.

65. **Auto-detect printer/IP on first run** (`SiteConfig.cs:65-130`). On first startup (no `config.json`), the Launcher auto-detects: (1) local IP via UDP socket to 8.8.8.8, (2) installed Konica printer by scanning `PrinterSettings.InstalledPrinters` for "KONICA" or "Minolta", (3) printer IP extracted from the printer name via regex. Detected values are written to `config.json` so subsequent runs use the saved config. If the config has the default IP (`192.168.1.160`), auto-detection re-runs and overwrites. `DetectAndApplyAutoSettings()` is called in `SiteConfig.Load()` and logs all detections at Information level.
