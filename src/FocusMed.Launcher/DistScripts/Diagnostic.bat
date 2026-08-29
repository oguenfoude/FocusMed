@echo off
:: FocusMed Diagnostic Script
echo.
echo ========================================
echo    FocusMed Diagnostic
echo ========================================
echo.

echo [1] Checking installation...
if exist "C:\Program Files\FocusMed\FocusMed.Launcher.exe" (
    echo   [OK] Launcher.exe exists
) else (
    echo   [ERROR] Launcher.exe not found
)

echo.
echo [2] Checking desktop shortcut...
if exist "%USERPROFILE%\Desktop\FocusMed.lnk" (
    echo   [OK] Desktop shortcut exists
    powershell -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('%USERPROFILE%\Desktop\FocusMed.lnk'); Write-Host '  Target:' $s.TargetPath; Write-Host '  WorkingDir:' $s.WorkingDirectory; Write-Host '  Icon:' $s.IconLocation"
) else (
    echo   [ERROR] Desktop shortcut not found
)

echo.
echo [3] Checking running processes...
tasklist /FI "IMAGENAME eq FocusMed.Launcher.exe" 2>nul | find /i "FocusMed.Launcher.exe" >nul
if %errorLevel% equ 0 (
    echo   [OK] Launcher is running
) else (
    echo   [WARN] Launcher is NOT running
)

tasklist /FI "IMAGENAME eq FocusMed.Worker.exe" 2>nul | find /i "FocusMed.Worker.exe" >nul
if %errorLevel% equ 0 (
    echo   [OK] Worker is running
) else (
    echo   [WARN] Worker is NOT running
)

tasklist /FI "IMAGENAME eq FocusMed.Dashboard.exe" 2>nul | find /i "FocusMed.Dashboard.exe" >nul
if %errorLevel% equ 0 (
    echo   [OK] Dashboard is running
) else (
    echo   [WARN] Dashboard is NOT running
)

echo.
echo [4] Checking ports...
netstat -an | find "11112" >nul
if %errorLevel% equ 0 (
    echo   [OK] Port 11112 (DICOM) is listening
) else (
    echo   [WARN] Port 11112 is NOT listening
)

netstat -an | find "5000" >nul
if %errorLevel% equ 0 (
    echo   [OK] Port 5000 (Dashboard) is listening
) else (
    echo   [WARN] Port 5000 is NOT listening
)

echo.
echo [5] Checking logs...
if exist "%LOCALAPPDATA%\FocusMed\logs\launcher-.log" (
    echo   [OK] Launcher log exists
    echo   Last 5 lines:
    powershell -Command "Get-Content '%LOCALAPPDATA%\FocusMed\logs\launcher-.log' -Tail 5"
) else (
    echo   [WARN] No launcher log found
)

echo.
echo [6] Testing Dashboard...
powershell -Command "try { $r = Invoke-WebRequest 'http://localhost:5000/health' -UseBasicParsing -TimeoutSec 5; Write-Host '  [OK] Dashboard responds:' $r.StatusCode } catch { Write-Host '  [ERROR] Dashboard not responding:' $_.Exception.Message }"

echo.
echo ========================================
echo    Diagnostic Complete
echo ========================================
echo.
echo If there are errors, try:
echo   1. Run as Administrator
echo   2. Check antivirus is not blocking
echo   3. Look at logs: %LOCALAPPDATA%\FocusMed\logs\
echo.
pause