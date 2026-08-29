# FocusMed

Multi-role DICOM Service Class Provider (SCP) on .NET 10 / SQLite. One TCP port handles C-STORE, C-ECHO, C-FIND, C-MOVE, Print Management, Storage Commitment, and Modality Worklist.

> Looking for AI-agent context (file:line gotchas, scope-per-request, etc.)? See [`AGENTS.md`](AGENTS.md).

## Quick Start

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Run

```powershell
dotnet build
dotnet run --project src/FocusMed.Worker     # DICOM listener TCP :11112
dotnet run --project src/FocusMed.Dashboard  # Blazor Server UI HTTP :5000
```

> **The Worker terminal must run as Administrator** — it binds TCP port `11112`.

On first startup, EF Core applies all migrations automatically via `Database.Migrate()`. The SQLite database `focusmed.db` is created in the working directory (or the `FOCUSMED_DB_CONNECTION` location).

### Startup Output

```
=== FocusMed Configuration ===
Data Directory: C:\Users\Administrator\AppData\Local\FocusMed
AE Title: FOCUSMED
Port: 11112
Bind Address: 0.0.0.0
Max PDU: 65536
AE Whitelist: Disabled
Print Merge Window: 300s
Storage Forward Targets configured: 0
PNG extraction enabled
DICOM listener successfully starting on 0.0.0.0:11112 as AE Title 'FOCUSMED'
```

## Architecture

```
src/
├── FocusMed.Data/               SQLite + EF Core. 11 entities, 4 enums, 2 migrations. No business logic. (net10.0)
├── FocusMed.Dicom/              fo-dicom-based SCP. Ingestion, MWL, Print, Storage Commitment, auto-merge. (net10.0)
├── FocusMed.Worker/             Top-level Program.cs, Serilog, DI, DICOM listener. (net10.0)
├── FocusMed.Dashboard/          Blazor Server UI (HTTP :5000). Browse/archive/delete studies, PDF preview + printing. (net10.0-windows)
├── FocusMed.Printing/           Print engine — Windows driver (XPS) + raw TCP, booklet imposition, Konica finishing. (net10.0-windows)
├── FocusMed.Printing.TestConsole/  Console diagnostic harness for the print engine. (net10.0-windows)
└── FocusMed.Launcher/           WPF supervisor — tray app, virtual "FocusMed" printer, resume-capture popup, restarts Worker + Dashboard. (net10.0-windows)
```

Dependency direction: `Worker` → `Dicom` → `Data` (leaf). `Dashboard` → `Data` + `Dicom` + `Printing`. `Launcher` → `Data`. `Printing` has no project dependencies. Solution is `FocusMed.slnx` (XML, not classic `.sln`).

## Dashboard

A separate Blazor Server project (InteractiveServer, all UI in French) for browsing received studies. It is **not** involved in DICOM ingestion — only the Worker receives via TCP port `11112`.

The Dashboard provides:
- **Études** (`/`) — studies list with search, date filter, pagination, soft-delete
- **Archives** (`/archives`) — archived studies with restore
- **Supprimées** (`/deleted`) — deleted studies with permanent delete (auto-purge after 30 days)
- **Study details** (`/study/{id}`) — patient info, image sidebar, layout controls, PDF preview iframe, lightbox, and **Imprimer** (Livret A3 / Plat A3 / Plat A4 via the `FocusMed.Printing` engine)

## Features

| DICOM Role | Notes |
|-----------|-------|
| **C-STORE** | Acquire images; automatic UID repair; per-frame on-demand PNG extraction; auto-merge prints into studies |
| **C-ECHO** | Verification |
| **C-FIND** | Patient / Study / Series queries against SQLite |
| **C-FIND (MWL)** | Modality Worklist (non-standard entry condition, see AGENTS.md) |
| **C-MOVE** | Send stored `.dcm` files to a move destination AE |
| **Storage Commitment** | N-ACTION received; N-EVENT-REPORT sent via reverse association with correct SOP Class UIDs from DB (requires per-site SCU mapping) |
| **Print Management** | N-CREATE/SET/ACTION/DELETE for Film Session/Box/Image Box; physical printing via `FocusMed.Printing` (Windows driver / raw TCP), decoupled from N-ACTION |

Other:
- Enriched association logging to `%LOCALAPPDATA%\FocusMed\logs\dicom_associations.log`
- Study completion detection via background polling (stable after `StudyStabilizationSeconds`)
- Graceful shutdown drain for storage forward queue
- Resume capture: print to the "FocusMed" virtual printer → WPF popup picks a study and attaches the PDF
- NuGet cache-friendly, zero build warnings

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
│   └── covers/                               # Cover page cache (24h TTL)
├── resumes/                                  # Launcher resume PDFs
└── logs/                                   # Serilog rolling + association log
```

Folders use `<Modality>` from DICOM tag (CT, MR, etc.) or `SC` for print images. All DICOM files stored in single `archive/` folder. Folder lookup uses `DicomImage.FilePath` from DB + `Directory.GetParent()` — never hash substring matching.

## Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `FOCUSMED_DATA` | Override data directory | `%LOCALAPPDATA%\FocusMed` |
| `FOCUSMED_DB_CONNECTION` | Override SQLite connection string | `Data Source=focusmed.db` |

## Entities

```
Patient (1) ──< Study (N) ──< Series (N) ──< DicomImage (N) ──< DicomFrame (N)
PrintJob (1) ──< FilmBox (N) ──< PrintImageBox (N)
StorageCommitmentJob (standalone) • WorklistEntry (standalone) • AssociationAuditEntry (standalone)
```

Enums: `StorageCommitmentStatus` (Pending/Completed/Failed), `PrintStatus` (Pending/Printing/Completed/Failed), `StudyStatus` (Receiving/Complete/Failed/Archived/Deleted), `AssociationOutcome` (Success/Rejected/Failed/PartiallyAccepted).

`DicomImage` includes `SopClassUid` (populated on ingest from DICOM `SOPClassUID` tag). `Study.CallingAeTitle` and `PrintJob.CallingAeTitle` are recorded on ingest and used by the print auto-merge. `Study.ResumePdfPath` points to the Launcher-assigned resume PDF.

## Configuration

All non-default config goes in `src/FocusMed.Worker/appsettings.json` (the Launcher regenerates a merged `appsettings.json` from `config.json` for deployed children).

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
| `StorageForwardTargets` | `[]` | C-STORE SCU forward targets (see below) |
| `PrintMergeWindowSeconds` | `300` | Window for auto-merging print images into existing studies |

### Other top-level keys

| Key | Default | Purpose |
|-----|---------|---------|
| `ConnectionString` | `Data Source=focusmed.db` | SQLite connection string |
| `StudyStabilizationSeconds` | `60` | Inactivity before study → Complete |
| `PngExtraction:Enabled` | `true` | On-demand PNG extraction toggle |

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
- `20260821215516_InitialSqlite`
- `20260829135437_PrintMergeSupport`

## Deployment

- `deploy/` (gitignored) is the live single-folder deployment: Worker + Dashboard + Launcher + merged `appsettings.json` + `config.json`. Republish it with `dotnet publish -c Release -o deploy` (Worker, Dashboard, Launcher), preserving the merged `appsettings.json`.
- Run it with `& "D:\FocusMed\deploy\FocusMed.Launcher.exe"` in an **Administrator** shell — the Launcher supervises Worker + Dashboard and runs the resume-print monitor.
- The `FocusMed.Launcher` is a WPF tray supervisor: starts Worker + Dashboard hidden, always restarts them, creates the "FocusMed" virtual printer (Microsoft Print To PDF on a Local Port), installs an ONLOGON autostart task, and opens a firewall rule for the DICOM port. Its `config.json` holds the site settings (AE title, ports, Konica raw printer IP, etc.).
- A client installer is built from `installer/FocusMed.iss` with Inno Setup 6 (output: `installer/FocusMedSetup-*.exe`; note the binary is >100 MB so it is gitignored — only the `.iss` script is tracked).

## Out of Scope (by design)

Until explicitly requested, FocusMed does **not** include `.docx` watchers.

## License

Internal — not yet licensed for external distribution.