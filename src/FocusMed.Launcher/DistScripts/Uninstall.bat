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

echo [1/5] Stopping FocusMed processes...
taskkill /IM FocusMed.Worker.exe /F >nul 2>&1
taskkill /IM FocusMed.Dashboard.exe /F >nul 2>&1
taskkill /IM FocusMed.Launcher.exe /F >nul 2>&1

echo [2/5] Removing shortcuts...
del "%USERPROFILE%\Desktop\FocusMed.lnk" >nul 2>&1
rmdir /S /Q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\FocusMed" >nul 2>&1

echo [3/5] Removing autostart task...
schtasks /Delete /TN "FocusMed" /F >nul 2>&1

echo [4/5] Removing firewall rule...
netsh advfirewall firewall delete rule name="FocusMed DICOM TCP 11112" >nul 2>&1

echo [5/5] Removing installation files...
rmdir /S /Q "C:\Program Files\FocusMed" >nul 2>&1

echo.
echo ========================================
echo    Uninstall Complete!
echo ========================================
echo.
echo Your data is preserved at:
echo   %LOCALAPPDATA%\FocusMed\
echo.
pause
