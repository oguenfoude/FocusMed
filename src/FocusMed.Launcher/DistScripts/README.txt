=====================================
  FOCUSMED - INSTALLATION GUIDE
=====================================

QUICK START (3 steps):
----------------------

1. Copy this entire "dist" folder to the target PC

2. Open the folder and RIGHT-CLICK "Install.bat"
   Select: "Run as administrator"

3. Done! FocusMed starts automatically.
   - Desktop icon: "FocusMed" (double-click to open)
   - System tray: Blue "F" icon (right-click for menu)
   - Dashboard: http://localhost:5000

WHAT GETS INSTALLED:
--------------------

Location: C:\Program Files\FocusMed\
Data:     %LOCALAPPDATA%\FocusMed\

Components:
- Launcher (system tray supervisor)
- Worker (DICOM listener, port 11112)
- Dashboard (web UI, port 5000)
- Virtual printer "FocusMed" (for resume capture)
- Autostart on login
- Firewall rule for DICOM

AUTO-DETECTED:
--------------
The installer automatically detects:
- Your local IP address
- Installed Konica printer
- Printer IP address

No manual configuration needed!

TO UNINSTALL:
-------------

Option 1: Run "Uninstall.bat" as administrator

Option 2: Control Panel > Programs > Uninstall FocusMed

WHAT'S INCLUDED:
----------------

FocusMed.Launcher.exe    - Main application (system tray)
FocusMed.Worker.exe      - DICOM listener
FocusMed.Dashboard.exe   - Web interface
config.json              - Configuration (auto-generated)
Install.bat              - This installer
Uninstall.bat            - Uninstaller
wwwroot/                 - Cover templates and assets
LatoFont/                - Printer fonts

REQUIREMENTS:
-------------

- Windows 10/11 (64-bit)
- Administrator rights (for installation)
- Network connection to Konica printer (for printing)

TROUBLESHOOTING:
----------------

Q: Dashboard won't open?
A: Wait 10 seconds after install, then try http://localhost:5000

Q: Port 11112 already in use?
A: Close other DICOM software, or change port in config.json

Q: Printer not detected?
A: Ensure Konica printer is installed in Windows first

Q: How to access from other computers?
A: Use http://YOUR-IP:5000 (find your IP in Command Prompt: ipconfig)

=====================================
  FocusMed - Medical Imaging System
=====================================