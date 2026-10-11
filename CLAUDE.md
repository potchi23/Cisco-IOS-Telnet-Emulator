# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Run commands from the repository root. The projects target .NET 9 (`net9.0`); install .NET SDK 9 or later.

```powershell
# Build both application and test projects
dotnet build .\SwitchTelnetEmulator.sln

# Run the emulator (default listener: 0.0.0.0:2323)
dotnet run --project .\SwitchTelnetEmulator

# Run the test harness
dotnet run --project .\SwitchTelnetEmulator.Tests

# Check formatting without changing files
dotnet format .\SwitchTelnetEmulator.sln --verify-no-changes

# Publish a self-contained Windows x64 executable
.\publish-windows.bat
```

There is no separate linter or test framework configured. `SwitchTelnetEmulator.Tests` is a console-based test harness; it runs all checks together and does not currently support selecting a single test by name. Its checks are in `SwitchTelnetEmulator.Tests/Program.cs`.

The app accepts `--host <IP>`, `--port <1-65535>`, and `--config <path>` to override listener and startup-configuration settings. `emulator-settings.json` beside the executable controls the default listener; if missing, defaults are written. The switch's persisted startup configuration is kept separately at `data/startup-config.json` beside the executable. Telnet is unencrypted; the default all-interface binding should only be used on trusted networks.

## Architecture

This is an educational, single-process Cisco-IOS-like Layer 2 switch CLI emulator, not a packet-forwarding switch or IOS implementation. The application is split into four cooperating areas under `SwitchTelnetEmulator/`:

- `Program.cs` loads and validates listener settings, applies command-line overrides, loads persisted switch configuration, creates the shared state/store, and starts the server.
- `Networking/TelnetServer.cs` accepts TCP clients and gives each connection its own `CliSession`; all sessions share the same `SwitchState` and `ConfigurationStore`. `Networking/TelnetConnection.cs` handles Telnet option negotiation, line endings, input editing, and text I/O.
- `Cli/CliSession.cs` owns per-client CLI mode and selected interface/VLAN. It parses commands, checks which commands are available in each mode, and delegates switch mutations and configuration rendering to the domain model. Keep command behavior and mode transitions here rather than in the socket layer.
- `Domain/SwitchState.cs` owns the synchronized running/startup configurations, default VLAN and 26-port inventory, validation of state changes, and IOS-style configuration rendering. It returns clones for reads so callers do not mutate shared state outside its lock.
- `Persistence/ConfigurationStore.cs` loads, validates, and saves switch configuration JSON; saves use a temporary file followed by replacement. `Persistence/EmulatorSettings.cs` is specifically for listener address/port settings, not switch state.

Saving (`copy running-config startup-config` / `write memory`) snapshots the current running configuration through `SwitchState.SaveStartupConfiguration`, persists it with `ConfigurationStore`, then updates the in-memory startup configuration. The test project references the app project and exercises CLI behavior, state persistence, VLAN safety, and listener settings without a third-party test framework.

The supported behavior and explicit simulator limitations are documented in `README.md`; consult it before expanding the command set or describing emulation fidelity.