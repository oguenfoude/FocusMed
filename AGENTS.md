# FocusMed — Agent Instructions

Compact reference for AI sessions. Every fact here is verified against the current codebase.

> Audience split: `AGENTS.md` = you (AI sessions). `README.md` = humans onboarding. These files do not duplicate each other.

## Project Shape

Three projects, single dependency direction: `Worker` → `Dicom` → `Data` (leaf).

| Project | TFM | Role |
|---------|-----|------|
| `FocusMed.Data` | `net10.0` | EF Core (`FocusMedDbContext`), 11 entities. No business logic. |
| `FocusMed.Dicom` | `net10.0` | `FocusMedScp` (single SCP), `DicomUpsertService`, hosted services, `PrintScuService`, `StorageForwardService`. |
| `FocusMed.Worker` | `net10.0-windows` | `Program.cs`, Serilog, DI wiring, `DicomListenerService`, `Spooler`. |

Solution: `FocusMed.slnx` (XML, **not** classic `.sln`).

## Commands

```powershell
dotnet build
dotnet run --project src/FocusMed.Worker
```

- Terminal **must be Administrator** — binds TCP port `11112`.
- PostgreSQL on `localhost:5432`, database `focusmed`. `Database.Migrate()` runs on startup.
- New EF migration (from `src/FocusMed.Data`): `dotnet ef migrations add <Name>`.
- No automated tests. Manual verification via DCMTK and `tools/` generators.

```powershell
storescu -v localhost 11112 path\to\image.dcm -aet YOUR_AET -aec FOCUSMED_SCP
echoscu  localhost 11112 -aet YOUR_AET -aec FOCUSMED_SCP
findscu  -v localhost 11112 -k QueryRetrieveLevel=STUDY -k PatientName="*" -aet YOUR_AET -aec FOCUSMED_SCP
movescu  -v localhost 11112 -k QueryRetrieveLevel=STUDY -k StudyInstanceUID=<uid> -aet YOUR_AET -aec FOCUSMED_SCP -aem YOUR_AET
```

## Runtime Data Layout

`data/` is gitignored. `PathHelper.GetDataDirectory()` walks up from `AppContext.BaseDirectory` looking for `FocusMed.slnx`; override via `FOCUSMED_DATA` env var.

```
data/
├── archive/<FNV-1a-Hash>/{study-info.json, <SeriesUid>/<SopUid>.dcm}
├── images/<FNV-1a-Hash>/<SeriesUid>/       # PNG per frame, 300 DPI
└── logs/                                   # Serilog rolling + association log
```

Hash is 64-bit FNV-1a of `StudyInstanceUID`, rendered as 16-char uppercase hex.

## Explicitly Out of Scope

Until the user explicitly directs otherwise, **do not** build: PDF generation, installers/MSIs, web dashboard/frontend, `.docx` watchers.

## PHI Warning

`tools/real_test/` contains real DICOM files with patient names in filenames. `tools/` is gitignored, but PHI exposure during development is real.

## AI Agent Gotchas

Each item is anchored to a verified file:line. Cite these before touching the listed code.

1. **`app.manifest` is disabled** (`src/FocusMed.Worker/FocusMed.Worker.csproj:5`). UAC elevation is **not** automatic — you still need an Administrator terminal to bind port 11112.

2. **`CStoreScp` does not exist.** All DICOM roles live in `FocusMedScp` (`src/FocusMed.Dicom/FocusMedScp.cs`). Do not reference `CStoreScp` or invent a separate class.

3. **`PathHelper` walks up from `AppContext.BaseDirectory`** looking for `FocusMed.slnx` to resolve `data/`. Works in dev (`bin/`); published builds fall back to `<AppContext.BaseDirectory>/data`. Override via `FOCUSMED_DATA` env var.

4. **Scope-per-request, not scope-per-singleton.** `DicomUpsertService` is a singleton but allocates a fresh `IServiceScope` (and `FocusMedDbContext`) **per file**. Concurrent C-STORE requests will collide on EF Core identity maps if you capture the DbContext in a field or convert the service to scoped.

5. **No `global.json`.** .NET 10.0 SDK is required but not pinned. Confirm the resolved SDK is 10.x before debugging build issues.

6. **`StudyCompletedEvent` publishes to nobody.** `InMemoryStudyEventBus` is wired in DI but has **zero subscribers**. Do not assume downstream effects; do not add feature code that waits on these events.

7. **Target framework split.** Worker = `net10.0-windows`, Data/Dicom = `net10.0`. `ScalingEngine` uses ImageSharp (cross-platform) but is resolved at runtime from `FocusMedScp`. `Spooler` (Windows-only `System.Drawing.Printing`) is dead code — kept as a file but no longer registered or used.

8. **fo-dicom transfer syntax names are non-standard.** Always pull `DicomTransferSyntax` / `DicomUID` static fields, never hand-type UIDs. The map in `FocusMedScp.cs` uses `JPEGProcess1`, `JPEGProcess2_4`, `JPEGProcess14`, `MPEG4AVCH264HighProfileLevel41` — not the human-friendly aliases.

9. **`DicomAssociation` exposes `RemoteHost`/`RemotePort` only** (`FocusMedScp.cs:72,125`). There is **no** `IPEndPoint` on the association — do not write `association.RemoteEndPoint`.

10. **Reject with `DicomRejectReason.NoReasonGiven`** (`FocusMedScp.cs:84`). The enum has no `Normal` member.

11. **Storage Commitment needs per-site config.** `DicomNetworking.StorageCommitmentScuMapping` must list every calling AE that expects N-EVENT-REPORT callbacks. Without a mapping, N-ACTION is accepted but the device never receives confirmation.

12. **MWL entry condition is non-standard** (`FocusMedScp.cs:202`). C-FIND routes to MWL when `QueryRetrieveLevel` is empty **or** `ScheduledProcedureStepSequence` is present — not the strict `QueryRetrieveLevel == "WORKLIST"` check. Test with real devices before assuming a non-standards-compliant SCU is broken.

13. **`Program.cs` re-binds `DicomNetworking` config** (`Program.cs:44-50`) into a fresh `DicomNetworkingOptions` so it can set `DicomServiceOptions.MaxPDULength`, instead of injecting `IOptions<DicomNetworkingOptions>` from DI. Works today; will silently diverge if the options class ever adds validation.

14. **`ScalingEngine.GetFilmDimensions`** supports A3, A4, 8INX10IN, 14INX17IN. Unknown `filmSize` falls back to A4 with a warning log. `filmSize == null` defaults to A4 silently.

## Quick File Index

- `src/FocusMed.Worker/Program.cs` — entry, Serilog, DI wiring, startup config summary, migration.
- `src/FocusMed.Worker/DicomListenerService.cs` — starts `IDicomServer<FocusMedScp>`.
- `src/FocusMed.Worker/PathHelper.cs` — resolves repo root / data dir.
- `src/FocusMed.Worker/Spooler.cs` — Windows print (dead code, kept for reference).
- `src/FocusMed.Worker/appsettings.json` — config.
- `src/FocusMed.Dicom/FocusMedScp.cs` — every DICOM role.
- `src/FocusMed.Dicom/DicomUpsertService.cs` — ingestion (UID repair, FNV-1a, PNG extract, forward queue enqueue).
- `src/FocusMed.Dicom/StudyCompletionService.cs` — 5s polling loop.
- `src/FocusMed.Dicom/StorageCommitmentScuService.cs` — 10s polling loop, N-EVENT-REPORT with DB-backed SOP Class lookup.
- `src/FocusMed.Dicom/PrintScuService.cs` — DICOM Print SCU.
- `src/FocusMed.Dicom/StorageForwardQueue.cs` — `Channel<T>`-based forward queue with `Complete()`/`PendingCount` for graceful shutdown.
- `src/FocusMed.Dicom/StorageForwardService.cs` — hosted C-STORE SCU, drains queue on `StopAsync`.
- `src/FocusMed.Dicom/ScalingEngine.cs` — print scaling with film size support (transient, has ILogger).
- `src/FocusMed.Dicom/Options/DicomNetworkingOptions.cs` — `DicomNetworking` section binding + `FilmPrinters`, `StorageForwardTargets`.
- `src/FocusMed.Data/FocusMedDbContext.cs` — 11 DbSets, fluent FK config.
- `src/FocusMed.Data/Migrations/` — 3 EF migrations (latest: `AddSopClassUidAndStudyInstanceUid`).
