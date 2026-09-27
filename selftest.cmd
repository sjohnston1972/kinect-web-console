@echo off
rem Checks every feature of Kinect Web Console using the fake sensor. Safe to run while the app is open:
rem it builds its own copy, uses a spare port and a throwaway folder, and never touches captures or settings.
setlocal
cd /d "%~dp0"
where pwsh >nul 2>nul
if %errorlevel%==0 (
  pwsh -NoProfile -ExecutionPolicy Bypass -File tools\selftest.ps1 %*
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\selftest.ps1 %*
)
echo.
pause
