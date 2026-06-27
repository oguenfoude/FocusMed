# FocusMed

FocusMed is a robust, multi-role DICOM Service Class Provider built on .NET 10. It handles C-STORE, C-ECHO, C-FIND, C-MOVE, and Print Management on a single TCP port with a PostgreSQL backend.

## Quick Setup Plan

1. **Install Prerequisites**: Ensure you have [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and PostgreSQL running locally (`localhost:5432`, db: `focusmed`).
2. **Build**: Run `dotnet build`.
3. **Run**: Launch an Administrator terminal and run `dotnet run --project src/FocusMed.Worker`. (Requires Admin to bind to TCP port 11112).
4. **Test**: Use DCMTK to test the service: `echoscu localhost 11112 -aet YOUR_AET -aec FOCUSMED_SCP`.

## Architecture

The solution is divided into three core projects:

- **`FocusMed.Data`**: Data access layer. PostgreSQL with Entity Framework Core. Contains the schema, entities, and messaging infrastructure. No business logic.
- **`FocusMed.Dicom`**: The DICOM receiver. `FocusMedScp` handles all service roles (C-STORE, C-ECHO, C-FIND, C-MOVE, Print Management). `DicomUpsertService` handles safe, parallelized DICOM ingestion with FNV-1a deterministic hashing.
- **`FocusMed.Worker`**: The application entry point. A Generic Host background service that orchestrates the DICOM listener, configures dependency injection, and manages structured logging via Serilog.

## Current Features
- Listens on port `11112` for all DICOM service roles
- **C-STORE**: Receives and archives DICOM images with automatic UID repair
- **C-ECHO**: Responds to verification requests
- **C-FIND**: Queries the database at Patient, Study, and Series levels
- **C-MOVE**: Sends stored DICOM files to a move destination
- **Print Management**: N-CREATE, N-SET, N-ACTION, N-DELETE for Film Session, Film Box, and Image Box entities
- Extracts PNG equivalents dynamically into `data/images/`
- Archives raw `.dcm` via FNV-1a deterministic hashing
- Study completion tracking via background polling service

## Strict Data Separation

FocusMed enforces a strict separation between source code and runtime data.

> [!WARNING]
> **The `data/` directory is intentionally ignored by version control to protect patient health information (PHI) and avoid committing logs.**

### Directory Layout
```text
FocusMed/
├── data/
│   ├── archive/        # Raw DICOM files sorted by <Hash>/<Series>/<SOP>
│   ├── images/         # Extracted PNGs per frame, sorted by <Hash>/<Series>/<SOP>
│   └── logs/           # Serilog rolling log files
├── src/
│   ├── FocusMed.Data/
│   ├── FocusMed.Dicom/
│   └── FocusMed.Worker/
```

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PostgreSQL](https://www.postgresql.org/) running on `localhost:5432` with database `focusmed`
- A DICOM testing tool (e.g., DCMTK: `storescu`, `echoscu`, `findscu`)

## Building and Running

1. **Build the solution**:
   ```powershell
   dotnet build
   ```

2. **Run the Worker**:
   > [!IMPORTANT]
   > **You must run your terminal as Administrator.** The app binds to TCP port 11112. The `app.manifest` requesting UAC elevation is currently commented out for automated headless testing.

   ```powershell
   dotnet run --project src/FocusMed.Worker
   ```

   On first startup, the app automatically creates/migrates the PostgreSQL schema.

## Testing

### C-STORE
```powershell
storescu -v localhost 11112 path/to/image.dcm -aet YOUR_AET -aec FOCUSMED_SCP
```

### C-ECHO
```powershell
echoscu localhost 11112 -aet YOUR_AET -aec FOCUSMED_SCP
```

### C-FIND
```powershell
findscu -v localhost 11112 -k QueryRetrieveLevel=STUDY -k PatientName="*" -aet YOUR_AET -aec FOCUSMED_SCP
```

### C-MOVE
```powershell
movescu -v localhost 11112 -k QueryRetrieveLevel=STUDY -k StudyInstanceUID=<uid> -aet YOUR_AET -aec FOCUSMED_SCP -aem YOUR_AET
```

## Configuration

Application settings are in `src/FocusMed.Worker/appsettings.json`:

| Key | Default | Purpose |
|-----|---------|---------|
| `ConnectionString` | `Host=localhost;Port=5432;Database=focusmed;Username=postgres;Password=postgres` | PostgreSQL connection |
| `DicomPort` | `11112` | TCP port for DICOM listener |
| `AETitle` | `FOCUSMED_SCP` | DICOM Application Entity title |
| `BindAddress` | `0.0.0.0` | Network interface to bind |
| `ArchivePath` | `%FOCUSMED_DATA%/archive` | Raw .dcm archive root |
| `StudyStabilizationSeconds` | `60` | Inactivity before study → Complete |
