@echo off
REM ============================================================
REM  Cutlance one-click build (double-click to run)
REM ============================================================
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo.
    echo [x] .NET SDK not found. Please install .NET 8 SDK:
    echo     https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

echo.
echo === [1/2] restore + build (Debug) ===
dotnet build vMixLite\Cutlance.csproj -c Debug
if errorlevel 1 (
    echo.
    echo [x] Build failed. Read the errors above.
    pause
    exit /b 1
)

echo.
echo === [2/2] launch ===
start "" "vMixLite\bin\Debug\net8.0-windows\Cutlance.exe"
echo Done.
