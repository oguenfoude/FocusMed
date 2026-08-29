@echo off
:: FocusMed Complete Installer - Run as Administrator

echo.
echo ========================================
echo    FocusMed - Medical Imaging System
echo ========================================
echo.
echo This will install FocusMed to:
echo   C:\Program Files\FocusMed\
echo.
echo It will create:
echo   - Desktop shortcut with icon
echo   - Start Menu shortcut  
echo   - Autostart on login
echo   - Virtual printer for resume capture
echo   - Firewall rule for DICOM
echo.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] This installer requires Administrator rights.
    echo Please right-click this file and select "Run as administrator"
    pause
    exit /b 1
)

echo [1/7] Creating installation directory...
if not exist "C:\Program Files\FocusMed" mkdir "C:\Program Files\FocusMed"

echo [2/7] Copying files...
xcopy /E /I /Y /Q "%~dp0*" "C:\Program Files\FocusMed\" >nul

echo [3/7] Creating desktop shortcut with icon...
powershell -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\FocusMed.lnk'); $s.TargetPath = 'C:\Program Files\FocusMed\FocusMed.Launcher.exe'; $s.WorkingDirectory = 'C:\Program Files\FocusMed'; $s.IconLocation = 'C:\Program Files\FocusMed\FocusMed.Launcher.exe,0'; $s.Description = 'FocusMed Medical Imaging'; $s.Save()"

echo [4/7] Creating Start Menu shortcut with icon...
if not exist "%APPDATA%\Microsoft\Windows\Start Menu\Programs\FocusMed" mkdir "%APPDATA%\Microsoft\Windows\Start Menu\Programs\FocusMed"
powershell -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut([Environment]::GetFolderPath('ApplicationData') + '\Microsoft\Windows\Start Menu\Programs\FocusMed\FocusMed.lnk'); $s.TargetPath = 'C:\Program Files\FocusMed\FocusMed.Launcher.exe'; $s.WorkingDirectory = 'C:\Program Files\FocusMed'; $s.IconLocation = 'C:\Program Files\FocusMed\FocusMed.Launcher.exe,0'; $s.Description = 'FocusMed Medical Imaging'; $s.Save()"

echo [5/7] Setting up autostart...
schtasks /Create /TN "FocusMed" /TR "\"C:\Program Files\FocusMed\FocusMed.Launcher.exe\" --autostart" /SC ONLOGON /RL HIGHEST /F >nul 2>&1

echo [6/7] Adding firewall rule...
netsh advfirewall firewall add rule name="FocusMed DICOM TCP 11112" dir=in action=allow protocol=TCP localport=11112 >nul 2>&1

echo [7/7] Starting FocusMed and opening Dashboard...
start "" "C:\Program Files\FocusMed\FocusMed.Launcher.exe"

timeout /t 5 /nobreak >nul

echo.
echo ========================================
echo    Installation Complete!
echo ========================================
echo.
echo FocusMed is now running:
echo   Dashboard:    http://localhost:5000
echo   DICOM Port:   11112
echo   System Tray:  Blue "F" icon
echo.
echo Desktop shortcut created with icon.
echo Double-click "FocusMed" on desktop to open Dashboard.
echo.
echo To uninstall, run: C:\Program Files\FocusMed\Uninstall.bat
echo.
pause
