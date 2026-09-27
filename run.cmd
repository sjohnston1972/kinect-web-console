@echo off
rem Starts Kinect Web Console with the real Kinect.
rem Builds the bridge first, so it always runs the latest code. Add --mock to use the fake sensor.
setlocal
cd /d "%~dp0"
title Kinect Web Console

set "DOTNET=dotnet"
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

echo Building the bridge...
"%DOTNET%" build bridge\KinectBridge.csproj -c Release -nologo -v q
if errorlevel 1 (
  echo.
  echo The build failed. The messages above say why. docs\SETUP.md lists what needs installing.
  pause
  exit /b 1
)

bridge\bin\KinectBridge.exe %*
