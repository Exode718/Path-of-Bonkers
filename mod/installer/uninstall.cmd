@echo off
setlocal
cd /d "%~dp0"

net session >nul 2>&1
if errorlevel 1 (
    echo Administrator rights are required to remove files from the game folder.
    echo Opening an elevated window...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
set "RESULT=%errorlevel%"

echo.
if not "%RESULT%"=="0" (
    echo Uninstall FAILED ^(code %RESULT%^).
) else (
    echo Uninstall complete.
)
pause
exit /b %RESULT%
