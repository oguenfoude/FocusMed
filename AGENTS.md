# FocusMed Architecture & Boundaries

## Core Principles
1. **Strict Separation of Concerns**: Each major feature lives in its own project under `src/`.
2. **Minimal Foundation**: Do not build features until they are explicitly required.
3. **Robustness over Cleverness**: Prefer reliable, observable code over premature abstraction.

## Project Structure
- `FocusMed.Data`: Data access layer (SQLite, EF Core). Contains the schema, interceptors (WAL mode), and generic repositories. No business logic here.
- `FocusMed.Dicom`: The DICOM receiver. Contains `CStoreScp`, `DicomUpsertService` for safe, parallelized DICOM ingestion, and `StudyCompletionService` for tracking study status.
- `FocusMed.Worker`: The entry point (Generic Host). Configures Serilog, holds `appsettings.json`, and hosts the background listener.

## Explicitly Out of Scope
Until explicitly directed by the user, **DO NOT** create or implement:
- PDF generation or printing logic.
- Installers, deployment scripts, MSIs, or Inno Setup configs.
- Web dashboard, Razor Pages, or frontend of any kind.
- Document (.docx) watchers/converters.

## Guidelines for Future AI Sessions
- **Respect Boundaries**: If a feature is requested that crosses domains, add a new project rather than bloating an existing one.
- **Observability**: Maintain rich structured logging (Serilog).
- **Concurrency**: `FocusMed.Dicom` uses a per-study locking mechanism (`ConcurrentDictionary<string, SemaphoreSlim>`) to allow parallel ingestion of different studies while preventing DB deadlocks for images within the same study. Do not break this pattern without explicit instruction.
