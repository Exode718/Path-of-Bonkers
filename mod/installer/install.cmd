@echo off
setlocal
cd /d "%~dp0"

net session >nul 2>&1
if errorlevel 1 (
    echo Administrator rights are required to write into the game folder.
    echo Opening an elevated window...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set "RESULT=%errorlevel%"

echo.
if not "%RESULT%"=="0" (
    echo Installation FAILED ^(code %RESULT%^).
) else (
    echo Installation complete.
)
pause
exit /b %RESULT%
