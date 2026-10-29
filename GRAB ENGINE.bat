@echo off
setlocal
cd /d "%~dp0"
title GRAB ENGINE

rem Always rebuild when the .NET SDK is available, so a stale app folder can never
rem hide the latest editor, textures or start/finish fixes. If no SDK is installed,
rem fall back to the last working published build.
set CAN_BUILD=0
where dotnet >nul 2>nul
if not errorlevel 1 (
    dotnet --list-sdks 2>nul | findstr /r "." >nul
    if not errorlevel 1 set CAN_BUILD=1
)

if "%CAN_BUILD%"=="1" (
    echo Building GRAB ENGINE...
    dotnet publish "src\GRABEngine\GRABEngine.csproj" -c Release -o "app" --nologo
    if errorlevel 1 (
        echo.
        echo Build failed. Launching the last working build instead.
        echo.
    )
)

if not exist "app\GRABEngine.exe" (
    echo GRAB ENGINE has not been built yet and no usable .NET SDK was found.
    echo Install the .NET 10 SDK, then run this file again.
    pause
    exit /b 1
)

start "" "app\GRABEngine.exe" %*
exit /b 0
