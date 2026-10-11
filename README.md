# SwitchTelnetEmulator

An educational emulator of a Cisco-like Layer 2 switch CLI. Practice common IOS-style commands over Telnet. **This is not Cisco IOS or Cisco Packet Tracer**: it does not forward Ethernet frames, perform STP convergence, or exchange BPDUs.

## Requirements

- .NET SDK 9 or later (the project targets `net9.0`; it was originally verified with SDK 9.0.102).

## Getting started

### Standalone Windows x64 executable

Run `publish-windows.bat` to publish a self-contained build to `publish\win-x64\SwitchTelnetEmulator.exe`. The target computer does not need .NET installed.

Keep `emulator-settings.json` beside the executable. Edit it with a text editor to change the listener settings. The default configuration is `0.0.0.0:2323`. When you launch the executable, a console window displays the server status; keep it open while using the emulator. Close the window to stop the server.

The listener configuration has this format:

```json
{
  "ListenAddress": "0.0.0.0",
  "Port": 2323
}
```

Listening on `0.0.0.0` accepts connections on all network interfaces. You may need to allow port 2323 through Windows Firewall. Telnet **does not encrypt traffic**; use it only on trusted networks. The switch configuration is stored separately in `data\startup-config.json`, next to the executable.

### Run from source

You can also start the emulator from PowerShell:

```powershell
dotnet run --project .\SwitchTelnetEmulator
```

Connect with PuTTY in **Telnet** mode to the computer's IP address on port `2323` (or `127.0.0.1` from the same computer). If the Windows Telnet client is installed, you can connect with:

```powershell
telnet 127.0.0.1 2323
```

The `--host`, `--port`, and `--config` arguments override the listener settings and configuration file path for that run.

## Supported modes and commands

Enter commands in English, as in IOS. Available interfaces are `FastEthernet0/1`–`FastEthernet0/24` and `GigabitEthernet0/1`–`GigabitEthernet0/2` (abbreviations `Fa0/n` and `Gi0/n` are also accepted). VLAN 1 is created by default.

```text
enable
configure terminal
hostname LabSwitch
vlan 10
 name Users
exit
interface FastEthernet0/1
 switchport mode access
 switchport access vlan 10
 description Workstation-1
 no shutdown
exit
interface GigabitEthernet0/1
 switchport mode trunk
 switchport trunk allowed vlan 1,10
end
show vlan brief
show interfaces status
show interfaces trunk
show interfaces switchport
show spanning-tree
show running-config
copy running-config startup-config
show startup-config
write memory
```

Other supported commands include `show version`, `shutdown`, `no shutdown`, `spanning-tree portfast`/`no spanning-tree portfast`, `no vlan <id>`, `exit`, `end`, `disable`, and `?`. Selected abbreviations are supported (for example, `en`, `conf t`, `int`, `sh vlan brief`, and `wr mem`); the emulator does not implement IOS's general ambiguous-abbreviation algorithm.

`show spanning-tree` provides a deterministic view of an isolated switch; it is not a topology or convergence simulation. Ports represent configuration state, not real active links. Routing, port-channels, security/authentication, ACLs, serial configuration commands, access to other devices, and many Packet Tracer command variants are not included.

## Build and test

```powershell
dotnet build .\SwitchTelnetEmulator.sln

dotnet run --project .\SwitchTelnetEmulator.Tests
```
