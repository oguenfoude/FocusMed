# FocusMed

Multi-role DICOM Service Class Provider (SCP) on .NET 10 / PostgreSQL. One TCP port handles C-STORE, C-ECHO, C-FIND, C-MOVE, Print Management, Storage Commitment, and Modality Worklist.

> Looking for AI-agent context (file:line gotchas, scope-per-request, etc.)? See [`AGENTS.md`](AGENTS.md).

## Quick Start

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- PostgreSQL on `localhost:5432`, database `focusmed` (created automatically)
- [DCMTK](https://dcmtk.org/) for testing (optional)

### Run

```powershell
dotnet build
dotnet run --project src/FocusMed.Worker
```

> **The terminal must run as Administrator.** The app binds to TCP port `11112`.

On first startup, EF Core applies all migrations automatically via `Database.Migrate()`. No manual SQL step.

### Startup Output

```
=== FocusMed Configuration ===
Data Directory: C:\Users\Administrator\AppData\Local\FocusMed
AE Title: FOCUSMED
Port: 11112
Bind Address: 0.0.0.0
Max PDU: 65536
AE Whitelist: Disabled
Film Printers configured: 0
Storage Forward Targets configured: 0
DICOM listener successfully starting on 0.0.0.0:11112 as AE Title 'FOCUSMED'
```

## Architecture

```
src/
├── FocusMed.Data/       PostgreSQL + EF Core. 11 entities. No business logic. (net10.0)
├── FocusMed.Dicom/      fo-dicom-based SCP. Ingestion, MWL, Print, Storage Commitment. (net10.0)
├── FocusMed.Worker/     Top-level Program.cs, Serilog, DI, listener. (net10.0)
├── FocusMed.Dashboard/  Blazor Server UI (HTTP :5000). Browse/delete/archive studies, PDF preview, **physical print button** (via PrintService). (net10.0)
├── FocusMed.PrintCapture/  WPF app — creates the "FocusMed" virtual printer, monitors print spooler, captures printed PDFs as studies. (net10.0-windows)
└── FocusMed.PrintService/  ASP.NET Core Minimal API (HTTP :5050). Physical printing to Windows-queue printers. (net10.0-windows)
```

Dependency direction: `Worker` → `Dicom` → `Data` (leaf). `Dashboard` → `Data` + `Dicom`. `PrintCapture` → `Data`. `PrintService` is **independent** of all projects — it's a separate OS process that talks to Dashboard only via HTTP. Solution is `FocusMed.slnx` (XML, not classic `.sln`).

## Dashboard

A separate Blazor Server project for browsing received studies. It is **not** involved in DICOM ingestion — only the Worker receives via TCP port `11112`. To run everything:

```powershell
dotnet run --project src/FocusMed.Worker          # TCP :11112
dotnet run --project src/FocusMed.Dashboard       # HTTP :5000
dotnet run --project src/FocusMed.PrintService     # HTTP :5050 (Windows only)
```

The Dashboard provides:
- **Études** (`/`) — studies list with search, date filter, pagination, soft-delete
- **Archives** (`/archives`) — archived studies with restore
- **Supprimées** (`/deleted`) — deleted studies with permanent delete (auto-purge after 30 days)
- **Imprimantes** (`/settings/printers`) — read-only list of configured printers
- **Study details** (`/study/{id}`) — patient info, image sidebar, A4-portrait PDF preview iframe, lightbox, **"Imprimer" button** at upper-right

> Printing from the Dashboard uses **`FocusMed.PrintService`** (Phase 1: A4 + optional duplex). Configure printers in `src/FocusMed.PrintService/appsettings.json`. DICOM-side Print Management is still implemented in `FocusMedScp.cs` and `PrintScuService.cs` — that's a separate system at the DICOM protocol level.

## Physical printing (FocusMed.PrintService)

A standalone Windows-only ASP.NET Core Minimal API on `http://localhost:5050`. It receives print jobs from the Dashboard and renders PDFs via **PdfiumPrinter** (which wraps Google's Pdfium + `System.Drawing.Printing.PrintDocument`). It deliberately does **not** use `System.Printing.PrintQueue.AddJob(path)` — sending raw PDF bytes through the spooler is the same bug class that killed the previous SumatraPDF-based path. Rasterising in-process means the printer sees an ordinary rendered bitmap stream instead of raw PDF.

Configure printers in `src/FocusMed.PrintService/appsettings.json`:

```json
{
  "Urls": "http://localhost:5050",
  "PhysicalPrinters": [
    {
      "Name": "Brother",
      "WindowsQueueName": "Brother HL-L2350DW series",
      "Protocol": "generic-driver",
      "Enabled": true
    }
  ]
}
```

- `WindowsQueueName` is **case-sensitive** and must match `Get-Printer | Select Name` byte-for-byte.
- Phase 1 implements the `generic-driver` protocol (A4 + optional duplex).
- Phase 2 will add a `konica-booklet` protocol implementation (Konica bizhub C250i with A3 booklet imposition + SNMP tray detection). Same `IPhysicalPrintService` interface, separate implementation class — **no Dashboard change required**.

At startup the service logs every Windows printer it sees and emits an `ERROR` log entry if any configured `WindowsQueueName` is missing (but the service stays up).

### Endpoints exposed to Dashboard

- `POST /print` — body `PrintRequest { PdfPath, PrinterName, Copies = 1, Duplex = false }` → `PrintResult { Success, JobId, ErrorMessage }`
- `GET /job-status/{printerName}/{jobId:int}` → `JobStatus { State, ErrorMessage }`
- `GET /printers` → `IReadOnlyList<PrinterInfo>`

All endpoints are HTTP-only on `localhost:5050`. CORS allows `localhost:5000` (the Dashboard).

## Features

| DICOM Role | Notes |
|-----------|-------|
| **C-STORE** | Acquire images; automatic UID repair; per-frame PNG extraction; FNV-1a archival |
| **C-ECHO** | Verification |
| **C-FIND** | Patient / Study / Series queries against PostgreSQL |
| **C-FIND (MWL)** | Modality Worklist against `WorklistEntries` |
| **C-MOVE** | Send stored `.dcm` files to a move destination AE |
| **Storage Commitment** | N-ACTION received; N-EVENT-REPORT sent via reverse association with correct SOP Class UIDs from DB (requires per-site SCU mapping) |
| **Print Management** | N-CREATE/SET/ACTION/DELETE for Film Session/Box/Image Box; multi-film-size support (A3, A4, 8INX10IN, 14INX17IN); decoupled execution via `PrintExecutionService` |

Other:
- Enriched association logging to `%LOCALAPPDATA%/FocusMed/logs/dicom_associations.log`
- Study completion detection via background polling
- Graceful shutdown drain for storage forward queue
- Startup config summary (AE title, port, printers, forward targets)
- Print decoupled: N-ACTION returns success immediately, physical print triggered separately

## Testing with DCMTK

```powershell
echoscu  localhost 11112 -aet YOUR_AET -aec FOCUSMED
storescu -v localhost 11112 path\to\image.dcm -aet YOUR_AET -aec FOCUSMED
findscu  -v localhost 11112 -k QueryRetrieveLevel=STUDY -k PatientName="*" -aet YOUR_AET -aec FOCUSMED
movescu  -v localhost 11112 -k QueryRetrieveLevel=STUDY -k StudyInstanceUID=<uid> -aet YOUR_AET -aec FOCUSMED -aem YOUR_AET
```

## Data Layout

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

## Entities

```
Patient (1) ──< Study (N) ──< Series (N) ──< DicomImage (N) ──< DicomFrame (N)
PrintJob (1) ──< FilmBox (N) ──< PrintImageBox (N)
StorageCommitmentJob (standalone) • WorklistEntry (standalone) • AssociationAuditEntry (standalone)
```

Unique indexes on every UID column (`StudyInstanceUid`, `SeriesInstanceUid`, `SopInstanceUid`).

`DicomImage` includes `SopClassUid` (populated on ingest from DICOM `SOPClassUID` tag). `WorklistEntry` includes `StudyInstanceUid` (generated and persisted on first MWL query). `StorageCommitmentJob.Status` is an enum (`StorageCommitmentStatus`: Pending=0, Completed=1, Failed=2) stored as integer.

## Configuration

All non-default config goes in `src/FocusMed.Worker/appsettings.json`.

### `DicomNetworking` section

| Key | Default | Purpose |
|-----|---------|---------|
| `AETitle` | `FOCUSMED` | DICOM Application Entity title (called) |
| `DicomPort` | `11112` | TCP port for listener |
| `MaxPduSize` | `65536` | Maximum PDU length in bytes |
| `BindAddress` | `0.0.0.0` | Network interface |
| `EnforceAeWhitelist` | `false` | When true, only `AllowedCallingAETitles` can associate |
| `SupportedTransferSyntaxes` | 10 entries | `ImplicitVRLittleEndian`, `ExplicitVRLittleEndian`, `JPEGLSLossless`, `JPEG2000Lossless`, `RLELossless`, `JPEGProcess1`, `JPEGProcess2_4`, `JPEGProcess14`, `MPEG2`, `MPEG4AVCH264HighProfileLevel41` |
| `AllowedCallingAETitles` | `[]` | `{AETitle, IPAddress}` allowlist |
| `StorageCommitmentScuMapping` | `{}` | Calling AE → `{Ip, Port}` for N-EVENT-REPORT callbacks |
| `FilmPrinters` | `[]` | DICOM Print SCU targets (see below) |
| `StorageForwardTargets` | `[]` | C-STORE SCU forward targets (see below) |

### Other top-level keys

| Key | Default | Purpose |
|-----|---------|---------|
| `ConnectionString` | `Host=localhost;Port=5432;Database=focusmed;Username=postgres;Password=admin` | PostgreSQL |
| `StudyStabilizationSeconds` | `60` | Inactivity before study → Complete |

### `FilmPrinters[]` (DICOM Print SCU)

Each entry represents a DICOM Printer SCP that can receive print jobs via the Print Management protocol.

| Property | Default | Purpose |
|----------|---------|---------|
| `Name` | `""` | Human-readable name (e.g. `"AlprintA3"`) |
| `ScuAe` | `""` | Our AE title when connecting to this printer |
| `PrinterIp` | `""` | Printer's IP address |
| `PrinterPort` | `0` | Printer's DICOM port |
| `PrinterAe` | `""` | Printer's AE title |
| `FilmTarget` | `"PROCESSOR"` | `FilmDestination` attribute |
| `FilmType` | `"PAPER"` | `MediumType` attribute |
| `PrinterType` | `"GrayLevel"` | `"GrayLevel"` or `"Multicolor"` — selects SOP Class for Image Box |
| `Enabled` | `true` | Whether this printer is active |

If no `FilmPrinters` entry matches (or all are disabled), the print job is rejected with `ProcessingFailure` and logged as an error.

### `StorageForwardTargets[]` (C-STORE SCU Auto-Forward)

Each entry represents an external Storage SCP that receives a copy of every incoming C-STORE image.

| Property | Default | Purpose |
|----------|---------|---------|
| `Name` | `""` | Human-readable name (e.g. `"ALCLOSE"`) |
| `AeTitle` | `""` | Target's AE title |
| `Ip` | `""` | Target's IP address |
| `Port` | `0` | Target's DICOM port |
| `ScuAe` | `""` | Our AE title when connecting (defaults to `AETitle` if empty) |
| `Enabled` | `true` | Whether this target is active |

Forwarding is queue-based and non-blocking. Failure on one target does not affect others.

## Migrations

Add a migration from `src/FocusMed.Data`:

```powershell
dotnet ef migrations add <Name> --project src/FocusMed.Data --startup-project src/FocusMed.Worker
```

Existing migrations are auto-applied on app startup. Current set:
- `20260627140620_AddStorageCommitmentAndWorklist`
- `20260627232933_AddAssociationAuditEntry`
- `20260705102645_AddSopClassUidAndStudyInstanceUid`
- `20260706214141_ConvertStorageCommitmentStatusToEnum`
- `20260708151541_MakePrintJobAndFilmBoxIdsNullable`
- `20260713161922_AddPatientAndStudyToPrintJob`
- `20260713164254_AllowDuplicateStudies`
- `20260713200338_AddDicomImageSource`
- `20260714181929_MakeDicomFramePngPathNullable`
- `20260714190917_AddFkIndexesAndStudyLastUpdatedAt`
- `20260714210848_RemoveUnusedIndexesAndPatientCreatedAt`
- `20260719114954_AddMetadataFields`
- `20260719181410_AddArchivedAndExcludeFromMerge`
- `20260719201409_RemoveExcludeFromMerge`
- `20260719205917_AddDeletedStatus`
- `20260721162736_AddUserPreferences`
- `20260721215348_UpdateUserPreferences`
- `20260722150452_AddDocumentEntity`
- `20260723140556_RemoveUserPreferencesAndDocuments`

## Out of Scope (by design)

Until explicitly requested, FocusMed does **not** include:
- Konica bizhub C250i-specific A3 booklet imposition + SNMP tray detection — Phase 2 of `FocusMed.PrintService` (separately implemented class, no Dashboard change)
- Installers/MSIs, `.docx` watchers, deployment scripts
- Installers / MSIs / deployment scripts
- `.docx` watchers or converters

## License

Internal — not yet licensed for external distribution.
