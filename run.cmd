@echo off
setlocal
title Process Radar

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Process Radar needs Administrator rights for live ETW tracing - elevating...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
echo Building and launching Process Radar...
dotnet run --project "ProcessRadar\ProcessRadar.csproj" -c Debug
if %errorlevel% neq 0 (
    echo.
    echo Build or run failed - see errors above.
    pause
)
