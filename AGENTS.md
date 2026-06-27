# FocusMed — Agent Instructions

Compact reference for AI sessions. Every section here is verified against the actual codebase.

## Core Principles
1. **Strict Separation of Concerns**: Each major feature lives in its own project under `src/`.
2. **Minimal Foundation**: Do not build features until explicitly required.
3. **Robustness over Cleverness**: Prefer reliable, observable code over premature abstraction.
4. **Data Separation**: All runtime data lives in `data/` at the repo root, never tracked by git.

## Project Structure
- `FocusMed.Data`: Data access layer (PostgreSQL + EF Core). DbContext, entities, messaging. No business logic.
- `FocusMed.Dicom`: DICOM receiver. `FocusMedScp` (multi-role SCP: C-STORE, C-ECHO, C-FIND, C-MOVE, Print Management), `DicomUpsertService` (ingestion engine, singleton), `StudyCompletionService` (background polling).
- `FocusMed.Worker`: Entry point (Generic Host). Top-level statements in `Program.cs`, Serilog config, DI wiring, `DicomListenerService` background service.

**Dependency graph**: `Worker` → `Dicom` → `Data` (leaf).

## Build & Run
```powershell
dotnet build
dotnet run --project src/FocusMed.Worker
```
- **Requires Administrator terminal** — the app binds to TCP port 11112.
- **Requires PostgreSQL** running on localhost:5432 with database `focusmed`.
- **Auto-migration** — the app calls `Database.Migrate()` on startup.
- **EF Migrations** (from `src/FocusMed.Data`): `dotnet ef migrations add <Name>`
- **Manual test** (requires running worker + DCMTK):
  ```powershell
  storescu -v localhost 11112 path/to/image.dcm -aet YOUR_AET -aec FOCUSMED_SCP
  echoscu localhost 11112 -aet YOUR_AET -aec FOCUSMED_SCP
  findscu -v localhost 11112 -k QueryRetrieveLevel=STUDY -k PatientName="*" -aet YOUR_AET -aec FOCUSMED_SCP
  ```
- **No automated tests exist.** Testing is manual via DCMTK tools and `tools/` test generators.

## Data Layout
```
data/
├── archive/<FNV-1a-Hash>/{study-info.json, <SeriesUid>/<SopUid>.dcm}
├── images/<FNV-1a-Hash>/<SeriesUid>/       # Extracted PNGs per frame
└── logs/                                   # Serilog rolling file logs
```

## Configuration
`src/FocusMed.Worker/appsettings.json`:
| Key | Default | Purpose |
|-----|---------|---------|
| `ConnectionString` | `Host=localhost;Port=5432;Database=focusmed;Username=postgres;Password=postgres` | PostgreSQL connection |
| `DicomPort` | `11112` | TCP port for DICOM listener |
| `AETitle` | `FOCUSMED_SCP` | DICOM Application Entity title |
| `BindAddress` | `0.0.0.0` | Network interface to bind |
| `ArchivePath` | `%FOCUSMED_DATA%/archive` | Raw .dcm archive root |
| `StudyStabilizationSeconds` | `60` | Inactivity before study → Complete |

## Code Style
- `net10.0` (Data/Dicom), `net10.0-windows` (Worker)
- `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>` in all projects
- File-scoped namespaces (`namespace FocusMed.Data;`)
- PascalCase for public members, `_camelCase` for private fields
- `I` prefix for interfaces, `Async` suffix for async methods
- Entity POCOs: `= null!` for nav props, `= string.Empty` for strings, `= new List<>()` for collections
- DI registration via `DependencyInjection` static classes with extension methods

## Explicitly Out of Scope
Until explicitly directed by the user, **DO NOT** create or implement:
- PDF generation or printing logic.
- Installers, deployment scripts, MSIs, or Inno Setup configs.
- Web dashboard, Razor Pages, or frontend of any kind.
- Document (.docx) watchers/converters.

## AI Agent Gotchas
1. **`app.manifest` is currently disabled.** The `<ApplicationManifest>app.manifest</ApplicationManifest>` line in `FocusMed.Worker.csproj` is commented out for automated headless testing. Do not assume elevated privileges are automatic.

2. **`CStoreScp` no longer exists.** It was replaced by `FocusMedScp` which implements all DICOM service roles. Do not reference `CStoreScp`.

3. **`PathHelper` walks up from `AppContext.BaseDirectory` looking for `FocusMed.slnx`** to find the repo root and resolve `data/`. In dev (running from `bin/`) this works. In published standalone builds, it falls back to `<AppContext.BaseDirectory>/data`.

4. **Scope-per-request pattern.** `DicomUpsertService` is singleton but creates a new `IServiceScope` per file to avoid EF Core tracking conflicts across concurrent requests.

5. **No `global.json`.** .NET 10.0 SDK is required but not pinned.

6. **`StudyCompletedEvent` is published but never consumed.** The `InMemoryStudyEventBus` is wired but has no subscribers. Infrastructure for future use.

7. **Target framework split.** Worker is `net10.0-windows`, Data and Dicom are `net10.0`. The Worker is Windows-only.

## PHI Warning
- `tools/real_test/` contains real-world DICOM files with patient names in filenames. While `tools/` is gitignored, be aware of PHI exposure during development.
