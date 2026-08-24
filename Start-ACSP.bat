@echo off
rem ACSP - Air Cargo Scheduling. Double-click launcher for Windows:
rem installs the .NET SDK if missing, uses the bundled HiGHS solver
rem (or IBM CPLEX when installed), starts the app and opens the browser.
setlocal EnableDelayedExpansion
title ACSP launcher
cd /d "%~dp0"

if not exist "Acsp.sln" (
  echo ERROR: Acsp.sln not found next to this script.
  pause
  exit /b 1
)
echo == ACSP launcher ==
echo project: %CD%

rem 1) .NET 8 SDK: system-wide, then per-user, else install per-user (no admin needed)
set "DOTNET=dotnet"
where dotnet >nul 2>nul
if errorlevel 1 (
  if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
    set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
  ) else (
    echo Installing the .NET 8 SDK into %USERPROFILE%\.dotnet ^(one time, a few minutes^)...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "iwr -UseBasicParsing https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1; & $env:TEMP\dotnet-install.ps1 -Channel 8.0 -InstallDir $env:USERPROFILE\.dotnet"
    set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
  )
)
for /f "delims=" %%v in ('"%DOTNET%" --version') do echo dotnet: %%v

rem 2) LP solver: CPLEX is picked up automatically when installed under
rem    C:\Program Files\IBM\ILOG; otherwise the bundled HiGHS library is used.
if exist "lib\win-x64\highs.dll" (
  set "ACSP_LIBHIGHS=%CD%\lib\win-x64\highs.dll"
  echo HiGHS: bundled ^(lib\win-x64\highs.dll^)
) else (
  echo WARNING: bundled HiGHS library not found; relying on CPLEX or a system HiGHS.
)

rem 3) open the browser as soon as the server answers, then run the server
start "" /b powershell -NoProfile -Command "for($i=0;$i -lt 300;$i++){try{Invoke-WebRequest -UseBasicParsing http://localhost:5170 -TimeoutSec 1 | Out-Null; Start-Process 'http://localhost:5170'; break}catch{Start-Sleep -Seconds 1}}"
echo Starting ACSP at http://localhost:5170 ... ^(first build takes a minute^)
echo Keep this window open; close it to stop the app.
"%DOTNET%" run --project src\Acsp.Web -c Release --no-launch-profile
pause
