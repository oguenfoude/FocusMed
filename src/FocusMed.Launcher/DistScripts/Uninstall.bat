@echo off
:: FocusMed Uninstaller - Run as Administrator

echo.
echo ========================================
echo    FocusMed Uninstaller
echo ========================================
echo.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] This uninstaller requires Administrator rights.
    pause
    exit /b 1
)

echo [1/6] Stopping FocusMed processes...
taskkill /IM FocusMed.Worker.exe /F >nul 2>&1
taskkill /IM FocusMed.Dashboard.exe /F >nul 2>&1
taskkill /IM FocusMed.Launcher.exe /F >nul 2>&1

echo [2/6] Removing desktop shortcut...
del "%USERPROFILE%\Desktop\FocusMed.lnk" >nul 2>&1

echo [3/6] Removing Start Menu shortcut...
rmdir /S /Q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\FocusMed" >nul 2>&1

echo [4/6] Removing autostart task...
schtasks /Delete /TN "FocusMed" /F >nul 2>&1

echo [5/6] Removing firewall rule...
netsh advfirewall firewall delete rule name="FocusMed DICOM TCP 11112" >nul 2>&1

echo [6/6] Removing installation files...
rmdir /S /Q "C:\Program Files\FocusMed" >nul 2>&1

echo.
echo ========================================
echo    Uninstall Complete!
echo ========================================
echo.
echo Your data is preserved at:
echo   %LOCALAPPDATA%\FocusMed\
echo.
echo To remove data, delete that folder manually.
echo.
pause
