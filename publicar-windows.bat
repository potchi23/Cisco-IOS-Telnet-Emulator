@echo off
setlocal
cd /d "%~dp0"

echo Publicando SwitchTelnetEmulator para Windows x64...
dotnet publish .\SwitchTelnetEmulator\SwitchTelnetEmulator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\win-x64
if errorlevel 1 (
    echo.
    echo Error al publicar. Comprueba que esta instalado el SDK de .NET 9 o posterior.
    pause
    exit /b 1
)

echo.
echo Listo: publish\win-x64\SwitchTelnetEmulator.exe
echo El fichero emulator-settings.json se publica junto al ejecutable.
pause
