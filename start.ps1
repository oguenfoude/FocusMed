#!/usr/bin/env pwsh
# start.ps1 — Starts both FocusMed Worker (DICOM :11112) and Dashboard (HTTP :5000)
# Run from repo root:  .\start.ps1

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "  ╔═══════════════════════════════════════════╗" -ForegroundColor DarkCyan
Write-Host "  ║         FocusMed — Démarrage               ║" -ForegroundColor DarkCyan
Write-Host "  ╚═══════════════════════════════════════════╝" -ForegroundColor DarkCyan
Write-Host ""

# Build once to catch errors early
Write-Host "[1/3] Compilation de la solution..." -ForegroundColor Yellow
dotnet build --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ La compilation a échoué." -ForegroundColor Red
    exit 1
}
Write-Host "  ✓ Compilation réussie (0 erreurs)" -ForegroundColor Green
Write-Host ""

# Start Worker in background
Write-Host "[2/3] Démarrage du Worker (DICOM TCP :11112)..." -ForegroundColor Yellow
$worker = Start-Process -FilePath "dotnet" `
    -ArgumentList "run --project src/FocusMed.Worker --no-build" `
    -WorkingDirectory $PSScriptRoot `
    -PassThru -NoNewWindow
Write-Host "  ✓ Worker PID: $($worker.Id)" -ForegroundColor Green
Write-Host ""

# Start Dashboard in foreground
Write-Host "[3/3] Démarrage du Dashboard (HTTP :5000)..." -ForegroundColor Yellow
Write-Host "  → Ouvrez http://localhost:5000 dans votre navigateur" -ForegroundColor Cyan
Write-Host ""

try {
    dotnet run --project src/FocusMed.Dashboard --no-build
}
finally {
    # When dashboard is stopped (Ctrl+C), also stop the worker
    if (!$worker.HasExited) {
        Write-Host ""
        Write-Host "Arrêt du Worker (PID $($worker.Id))..." -ForegroundColor Yellow
        Stop-Process -Id $worker.Id -Force -ErrorAction SilentlyContinue
        Write-Host "  ✓ Worker arrêté" -ForegroundColor Green
    }
    Write-Host "FocusMed arrêté." -ForegroundColor DarkCyan
}
