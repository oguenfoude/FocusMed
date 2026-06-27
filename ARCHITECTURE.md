# FocusMed — Architecture Deep Dive

Detailed internal design notes. For the compact agent reference, see `AGENTS.md`.

## System Overview

FocusMed is a multi-role DICOM SCP (Service Class Provider). It listens on a TCP port, accepts DICOM associations, and handles C-STORE, C-ECHO, C-FIND, C-MOVE, and Print Management (N-CREATE/N-SET/N-ACTION/N-DELETE) requests.

```
Modality/SCU ──TCP:11112──► DicomListenerService (HostedService)
                                    │
                                    ▼
                              FocusMedScp (fo-dicom)
                           ┌────────┼────────┐──────────┐
                           │        │        │          │
                      C-STORE    C-ECHO   C-FIND    C-MOVE
                           │                       Print Mgmt
                           ▼
                    DicomUpsertService (Singleton)
                   ┌────────┴────────┐
                   │                  │
             EF Core DB          File System
           (PostgreSQL)      (archive/ + images/)
                   │
                   ▼
            StudyCompletionService (HostedService, polls every 5s)
                   │
                   ▼
            IStudyEventBus (Channel<T>, no subscribers yet)
```

## Data Ingestion Pipeline

### 1. Connection Handling (`DicomListenerService`)
- Creates an `IDicomServer<FocusMedScp>` on the configured port/AE.
- Checks port availability at startup — logs fatal and returns early if port is in use.
- Each incoming DICOM association gets a new `FocusMedScp` instance (fo-dicom handles this per-connection).

### 2. SCP Handler (`FocusMedScp`)
Handles all DICOM service roles:
- **C-STORE**: Accepts all Storage categories. Delegates to `DicomUpsertService.ProcessDicomFileAsync`.
- **C-ECHO**: Returns Success immediately.
- **C-FIND**: Queries PostgreSQL for matching Patient/Study/Series. Yields `Pending` per match, then `Success`.
- **C-MOVE**: Looks up images in DB, sends stored `.dcm` files via `SendRequestAsync`.
- **Print Management**: N-CREATE/N-SET/N-ACTION/N-DELETE for Film Session, Film Box, Image Box entities.

Presentation context negotiation accepts: Storage, Verification, Query/Retrieve, Print Management.

### 3. Ingestion Engine (`DicomUpsertService` — singleton)
For each received DICOM file:

1. **UID Repair** — Synthesizes fallback values for missing PatientID, StudyInstanceUID, SeriesInstanceUID, SOPInstanceUID.
2. **Scope Creation** — Creates a new `IServiceScope` per request to get a fresh `FocusMedDbContext`.
3. **Entity Upsert** — Checks for existing Patient/Study/Series/DicomImage records by UID. Creates or updates as needed.
4. **FNV-1a Hash** — Computes a 64-bit hash of the `StudyInstanceUID` for deterministic directory naming.
5. **File Archival** — Writes raw `.dcm` to `data/archive/<hash>/<seriesUid>/<sopUid>.dcm` plus `study-info.json`.
6. **PNG Extraction** — Decodes pixel data via ImageSharp, exports each frame to `data/images/<hash>/<seriesUid>/`.
7. **Save** — Calls `SaveChangesAsync`.

### 4. Study Completion (`StudyCompletionService` — hosted background service)
- Polls every 5 seconds.
- Queries for studies with `Status == Receiving` whose `LastUpdatedAt` is older than `StudyStabilizationSeconds` (default 60s).
- Transitions them to `Status.Complete`.
- Publishes a `StudyCompletedEvent` on the event bus (currently has no subscribers).

## Concurrency Model

- **PostgreSQL MVCC**: Write contention handled natively by the database. No application-level locks needed.
- **Scope-per-request**: Each DICOM file gets its own `IServiceScope` → `FocusMedDbContext`. Prevents EF Core identity resolution conflicts across concurrent requests.

## Entity Relationship

```
Patient (1) ──< Study (N)
Study (1) ──< Series (N)
Series (1) ──< DicomImage (N)
DicomImage (1) ──< DicomFrame (N)

PrintJob (1) ──< FilmBox (N)
FilmBox (1) ──< PrintImageBox (N)
```

- `StudyInstanceUid`, `SeriesInstanceUid`, `SopInstanceUid`, `PrintJob.SopInstanceUid`, `FilmBox.SopInstanceUid`, `PrintImageBox.SopInstanceUid` have unique indexes.
- `Status`, `CreatedAt` on `Studies` and `PatientId` on `Patients` have performance indexes.
- FK cascades: PrintJob → FilmBox → PrintImageBox.

## DI Registration Order

```
services.AddFocusMedData(connectionString)    // From FocusMed.Data.DependencyInjection
  → FocusMedDbContext (scoped, PostgreSQL)
  → IStudyEventBus / InMemoryStudyEventBus (singleton)

services.AddFocusMedDicom()                   // From FocusMed.Dicom.DependencyInjection
  → DicomUpsertService (singleton)
  → StudyCompletionService (hosted)

services.AddFellowOakDicom()
  → ImageSharpImageManager (transcoder)
  → NativeTranscoderManager (JPEG 2000 etc.)

services.AddHostedService<DicomListenerService>()
```

## Tools Directory (gitignored)

| Path | Purpose |
|------|---------|
| `tools/generator/` | .NET console app generating synthetic DICOM test files |
| `tools/burst/burst_0..49.dcm` | 50 pre-generated burst test files |
| `tools/real_test/` | 178 real-world DICOM files (**contains patient PHI in filenames**) |
| `tools/bad_uids.dcm` | DICOM with missing UIDs for testing repair logic |
| `tools/jpeg2000.dcm` | JPEG 2000 transcoded file for codec testing |
| `tools/reflect.cs` | Standalone fo-dicom assembly reflection script |
| `check.cs` (repo root) | Standalone `IDicomServerFactory.Create` signature reflection |
