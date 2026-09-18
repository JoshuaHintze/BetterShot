@echo off
setlocal
set "APP=%~dp0BetterShot\bin\Release\net10.0-windows\BetterShot.exe"
set "LOCAL_DOTNET_ROOT=%~dp0.tools\dotnet"

if not exist "%APP%" (
  echo BetterShot has not been built yet. Run build.ps1 first.
  pause
  exit /b 1
)

if exist "%LOCAL_DOTNET_ROOT%\dotnet.exe" (
  set "DOTNET_ROOT=%LOCAL_DOTNET_ROOT%"
  set "DOTNET_ROOT_X64=%LOCAL_DOTNET_ROOT%"
)
start "" "%APP%"
