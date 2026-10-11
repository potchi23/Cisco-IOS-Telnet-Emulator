@echo off
setlocal
cd /d "%~dp0"

echo Publishing SwitchTelnetEmulator for Windows x64...
dotnet publish .\SwitchTelnetEmulator\SwitchTelnetEmulator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\win-x64
if errorlevel 1 (
    echo.
    echo Publish failed. Make sure the .NET 9 SDK or later is installed.
    pause
    exit /b 1
)

echo.
echo Done: publish\win-x64\SwitchTelnetEmulator.exe
echo The emulator-settings.json file is published next to the executable.
pause
