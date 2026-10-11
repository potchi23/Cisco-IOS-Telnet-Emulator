using System.Text;
using System.Text.RegularExpressions;
using SwitchTelnetEmulator.Domain;
using SwitchTelnetEmulator.Persistence;

namespace SwitchTelnetEmulator.Cli;

public enum CliMode { User, Privileged, GlobalConfiguration, InterfaceConfiguration, VlanConfiguration, LineConfiguration }

public sealed class CliSession(SwitchState state, ConfigurationStore store)
{
    private CliMode _mode = CliMode.User;
    private string? _selectedPort;
    private int? _selectedVlan;
    private bool _collectingMotd;
    private char _motdDelimiter;
    private bool _motdFailed;
    private readonly StringBuilder _motdLines = new();

    public string Prompt => _mode switch
    {
        CliMode.User => $"{state.Hostname}>",
        CliMode.Privileged => $"{state.Hostname}#",
        CliMode.GlobalConfiguration => $"{state.Hostname}(config)#",
        CliMode.InterfaceConfiguration => $"{state.Hostname}(config-if)#",
        CliMode.VlanConfiguration => $"{state.Hostname}(config-vlan)#",
        CliMode.LineConfiguration => $"{state.Hostname}(config-line)#",
        _ => $"{state.Hostname}>"
    };

    public string Execute(string input)
    {
        if (_collectingMotd)
        {
            var captureResult = CaptureMotdLine(input);
            return captureResult is null ? string.Empty : captureResult;
        }
        var line = input.Trim();
        if (line.Length == 0) return string.Empty;
        if (line is "?" or "help") return Help();
        if (_mode is CliMode.GlobalConfiguration && line.StartsWith("banner motd ", StringComparison.OrdinalIgnoreCase))
            return BeginMotd(line);
        if (line.EndsWith('?')) return Help(line[..^1].Trim());

        var tokens = Tokenize(line);
        if (tokens.Count == 0) return string.Empty;
        var command = tokens[0].ToLowerInvariant();
        var args = tokens.Skip(1).ToList();
        if (Is(command, "exit", "ex")) return Exit();
        if (Is(command, "end", "end") && _mode is not (CliMode.User or CliMode.Privileged))
        {
            _mode = CliMode.Privileged;
            _selectedPort = null;
            _selectedVlan = null;
            return string.Empty;
        }
        if (command == "end") return "% Invalid input detected at '^' marker.";

        return _mode switch
        {
            CliMode.User => ExecuteUser(command, args),
            CliMode.Privileged => ExecutePrivileged(command, args),
            CliMode.GlobalConfiguration => ExecuteGlobal(command, args),
            CliMode.InterfaceConfiguration => ExecuteInterface(command, args),
            CliMode.VlanConfiguration => ExecuteVlan(command, args),
            CliMode.LineConfiguration => ExecuteLine(command, args),
            _ => "% Invalid command."
        };
    }

    private string ExecuteUser(string command, List<string> args)
    {
        if (Is(command, "enable", "en"))
        {
            if (args.Count != 0) return "% Invalid input detected.";
            _mode = CliMode.Privileged;
            return string.Empty;
        }
        if (Is(command, "show", "sh")) return Show(args, privileged: false);
        return "% Invalid input detected at '^' marker.";
    }

    private string ExecutePrivileged(string command, List<string> args)
    {
        if (Is(command, "configure", "conf"))
        {
            if (args.Count == 1 && Is(args[0], "terminal", "t"))
            {
                _mode = CliMode.GlobalConfiguration;
                return "Enter configuration commands, one per line. End with CNTL/Z.";
            }
            return "% Invalid input detected.";
        }
        if (Is(command, "show", "sh")) return Show(args, privileged: true);
        if (Is(command, "copy", "cop") && args.Count == 2 && Is(args[0], "running-config", "run") && Is(args[1], "startup-config", "start"))
        {
            Save();
            return "Building configuration...\n[OK]";
        }
        if (Is(command, "write", "wr") && (args.Count == 0 || (args.Count == 1 && Is(args[0], "memory", "mem"))))
        {
            Save();
            return "Building configuration...\n[OK]";
        }
        if (Is(command, "vlan", "vl") && args.Count == 0) return Show(new() { "vlan", "brief" }, true);
        if (Is(command, "disable", "dis"))
        {
            _mode = CliMode.User;
            return string.Empty;
        }
        return "% Invalid input detected at '^' marker.";
    }

    private string ExecuteGlobal(string command, List<string> args)
    {
        if (Is(command, "no", "no") && args.Count == 1 && Is(args[0], "hostname", "host"))
            return state.TrySetHostname("Switch", out var hostnameError) ? string.Empty : hostnameError;
        if (Is(command, "hostname", "host") && args.Count == 1)
            return state.TrySetHostname(args[0], out var error) ? string.Empty : error;
        if (Is(command, "vlan", "vl") && args.Count == 1 && int.TryParse(args[0], out var id))
        {
            if (!state.TryCreateVlan(id, out var error) && !state.VlanExists(id)) return error;
            _selectedVlan = id;
            _mode = CliMode.VlanConfiguration;
            return string.Empty;
        }
        if (Is(command, "no", "no") && args.Count == 2 && Is(args[0], "vlan", "vl") && int.TryParse(args[1], out var vlanId))
            return state.TryDeleteVlan(vlanId, out var error) ? string.Empty : error;
        if (command == "no" && args.Count == 2 && Is(args[0], "banner", "ban") && Is(args[1], "motd", "motd"))
            return state.TrySetMotd(string.Empty, out var bannerError) ? string.Empty : bannerError;
        if (Is(command, "line", "li") && Is(args.FirstOrDefault() ?? string.Empty, "vty", "vty") &&
            (args.Count == 2 && args[1] == "0" || args.Count == 3 && args[1] == "0" && args[2] == "4"))
        {
            _mode = CliMode.LineConfiguration;
            return string.Empty;
        }
        if (Is(command, "line", "li")) return "% Only line vty 0 or line vty 0 4 is supported.";
        if (Is(command, "interface", "int") && args.Count == 1)
        {
            if (!state.PortExists(args[0])) return $"% Invalid interface '{args[0]}'.";
            state.TryGetPort(args[0], out var port);
            _selectedPort = port.Name;
            _mode = CliMode.InterfaceConfiguration;
            return string.Empty;
        }
        return "% Invalid input detected at '^' marker.";
    }

    private string ExecuteLine(string command, List<string> args)
    {
        if (Is(command, "password", "pass") && args.Count == 1)
            return state.TrySetVtyPassword(args[0], out var error) ? string.Empty : error;
        if (Is(command, "login", "log") && args.Count == 0)
        {
            state.SetVtyLogin(true);
            return string.Empty;
        }
        if (command == "no" && args.Count == 1 && Is(args[0], "login", "log"))
        {
            state.SetVtyLogin(false);
            return string.Empty;
        }
        if (command == "no" && args.Count == 1 && Is(args[0], "password", "pass"))
        {
            state.ClearVtyPassword();
            return string.Empty;
        }
        return "% Invalid input detected at '^' marker.";
    }

    private string ExecuteVlan(string command, List<string> args)
    {
        if (_selectedVlan is not int vlan) return "% No VLAN selected.";
        if (Is(command, "name", "na") && args.Count > 0)
            return state.TrySetVlanName(vlan, string.Join(' ', args), out var error) ? string.Empty : error;
        return "% Invalid input detected at '^' marker.";
    }

    private string ExecuteInterface(string command, List<string> args)
    {
        if (_selectedPort is null) return "% No interface selected.";
        if (Is(command, "description", "des") && args.Count > 0)
        {
            var description = string.Join(' ', args);
            if (description.Length > 80) return "% Description is limited to 80 characters.";
            return UpdatePort(port => port.Description = description);
        }
        if (Is(command, "shutdown", "shut") && args.Count == 0) return UpdatePort(port => port.AdministrativelyDown = true);
        if (Is(command, "no", "no") && args.Count == 1 && Is(args[0], "shutdown", "shut")) return UpdatePort(port => port.AdministrativelyDown = false);
        if (Is(command, "switchport", "sw") && args.Count == 1 && Is(args[0], "nonegotiate", "none")) return "% DTP negotiation is not simulated.";
        if (Is(command, "spanning-tree", "span") && args.Count == 1 && Is(args[0], "portfast", "portf")) return UpdatePort(port => port.PortFast = true);
        if (Is(command, "no", "no") && args.Count == 2 && Is(args[0], "spanning-tree", "span") && Is(args[1], "portfast", "portf")) return UpdatePort(port => port.PortFast = false);
        if (Is(command, "switchport", "sw")) return Switchport(args);
        return "% Invalid input detected at '^' marker.";
    }

    private string Switchport(List<string> args)
    {
        if (args.Count == 2 && Is(args[0], "mode", "mo") && Is(args[1], "access", "acc")) return UpdatePort(port => port.Mode = "access");
        if (args.Count == 2 && Is(args[0], "mode", "mo") && Is(args[1], "trunk", "tr")) return UpdatePort(port => port.Mode = "trunk");
        if (args.Count == 3 && Is(args[0], "access", "acc") && Is(args[1], "vlan", "vl") && int.TryParse(args[2], out var accessVlan))
            return state.TrySetAccessVlan(_selectedPort!, accessVlan, out var error) ? string.Empty : error;
        if (args.Count == 4 && Is(args[0], "trunk", "tr") && Is(args[1], "allowed", "all") && Is(args[2], "vlan", "vl"))
        {
            if (!TryParseVlanList(args[3], out var vlans)) return "% Invalid VLAN list.";
            return state.TrySetAllowedVlans(_selectedPort!, vlans, out var error) ? string.Empty : error;
        }
        return "% Invalid switchport command.";
    }

    private string BeginMotd(string line)
    {
        var value = line["banner motd ".Length..];
        if (value.Length == 0) return "% Incomplete command.";
        _motdDelimiter = value[0];
        var close = value.IndexOf(_motdDelimiter, 1);
        if (close >= 0)
            return state.TrySetMotd(value[1..close], out var error) ? string.Empty : error;
        _collectingMotd = true;
        _motdLines.Clear();
        if (value.Length > 1) _motdLines.Append(value[1..]).Append('\n');
        return "Enter TEXT message. End with the delimiter.";
    }

    private string? CaptureMotdLine(string line)
    {
        if (_motdFailed)
        {
            if (line.Contains(_motdDelimiter))
            {
                _collectingMotd = false;
                _motdFailed = false;
                _motdLines.Clear();
                return "% MOTD must be at most 4096 characters.";
            }
            return null;
        }

        var close = line.IndexOf(_motdDelimiter);
        if (close >= 0)
        {
            _motdLines.Append(line[..close]);
            _collectingMotd = false;
            var banner = _motdLines.ToString().TrimEnd((char)13, (char)10);
            _motdLines.Clear();
            return state.TrySetMotd(banner, out var error) ? string.Empty : error;
        }
        _motdLines.Append(line).Append('\n');
        if (_motdLines.Length > 4096)
        {
            _motdLines.Clear();
            _motdFailed = true;
            return null;
        }
        return string.Empty;
    }

    private string Show(List<string> args, bool privileged)
    {
        if (args.Count == 0) return "% Incomplete command.";
        if (Is(args[0], "running-config", "run") && args.Count == 1)
        {
            if (!privileged) return "% Authorization failed.";
            return "Building configuration...\n!\n" + SwitchState.RenderConfiguration(state.GetRunningConfiguration());
        }
        if (Is(args[0], "startup-config", "start") && args.Count == 1)
        {
            if (!privileged) return "% Authorization failed.";
            return "!\n" + SwitchState.RenderConfiguration(state.GetStartupConfiguration());
        }
        if (Is(args[0], "version", "ver") && args.Count == 1)
            return "SwitchTelnetEmulator Educational L2 Switch\nCisco IOS-like CLI emulator; no real forwarding or IOS image.\nSystem image file is \"flash:educational-switch.bin\".\n26 Ethernet interfaces";
        if (Is(args[0], "vlan", "vl") && (args.Count == 1 || (args.Count == 2 && Is(args[1], "brief", "br")))) return ShowVlans();
        if (Is(args[0], "interfaces", "int"))
        {
            if (args.Count == 1 || (args.Count == 2 && Is(args[1], "status", "st"))) return ShowInterfaces();
            if (args.Count == 2 && Is(args[1], "trunk", "tr")) return ShowTrunks();
            if (args.Count == 2 && Is(args[1], "switchport", "sw")) return ShowSwitchport();
            if (args.Count == 3 && Is(args[1], "switchport", "sw")) return ShowSwitchport(args[2]);
        }
        if (Is(args[0], "spanning-tree", "span")) return ShowSpanningTree();
        return "% Invalid input detected at '^' marker.";
    }

    private string ShowVlans()
    {
        var output = new StringBuilder("VLAN Name                             Status    Ports\n");
        foreach (var vlan in state.GetVlans())
        {
            var ports = state.GetPorts().Where(p => p.Mode == "access" && p.AccessVlan == vlan.Id).Select(p => ShortPort(p.Name));
            output.AppendLine($"{vlan.Id,-4} {vlan.Name,-32} active    {string.Join(',', ports)}");
        }
        return output.ToString().TrimEnd();
    }

    private string ShowInterfaces()
    {
        var output = new StringBuilder("Port                Name                 Status       Vlan       Duplex Speed Type\n");
        foreach (var port in state.GetPorts())
        {
            var status = port.AdministrativelyDown ? "disabled" : "connected";
            var vlan = port.Mode == "trunk" ? "trunk" : port.AccessVlan.ToString();
            output.AppendLine($"{ShortPort(port.Name),-19} {port.Description,-20} {status,-12} {vlan,-10} a-full   a-100  10/100BaseTX");
        }
        return output.ToString().TrimEnd();
    }

    private string ShowSwitchport(string? interfaceName = null)
    {
        var ports = state.GetPorts().Where(port => interfaceName is null || port.Name == SwitchState.CanonicalPortName(interfaceName)).ToArray();
        if (ports.Length == 0) return "% Invalid interface.";
        return string.Join("\n\n", ports.Select(port =>
            $"Name: {port.Name}\nSwitchport: Enabled\nAdministrative Mode: {port.Mode}\nOperational Mode: {(port.AdministrativelyDown ? "down" : port.Mode)}\nAccess Mode VLAN: {port.AccessVlan} ({state.GetVlans().FirstOrDefault(v => v.Id == port.AccessVlan)?.Name ?? "unknown"})\nTrunking VLANs Enabled: {SwitchState.FormatAllowedVlans(port)}"));
    }

    private string ShowTrunks()
    {
        var trunks = state.GetPorts().Where(p => p.Mode == "trunk" && !p.AdministrativelyDown).ToArray();
        var output = new StringBuilder("Port        Mode         Encapsulation  Status        Native vlan\n");
        foreach (var port in trunks) output.AppendLine($"{ShortPort(port.Name),-11} on           802.1q         trunking      1");
        output.AppendLine("\nPort        Vlans allowed on trunk");
        foreach (var port in trunks) output.AppendLine($"{ShortPort(port.Name),-11} {SwitchState.FormatAllowedVlans(port)}");
        return output.ToString().TrimEnd();
    }

    private string ShowSpanningTree()
    {
        var output = new StringBuilder("Spanning tree enabled protocol ieee\n");
        foreach (var vlan in state.GetVlans())
        {
            output.AppendLine($"\nVLAN{vlan.Id}\n  Spanning tree enabled protocol ieee\n  Root ID    Priority    32769\n             Address     0000.0000.0001\n  Bridge ID  Priority    32769 (priority 32768 sys-id-ext {vlan.Id})\n             Address     0000.0000.0001\n\nInterface           Role Sts Cost      Prio.Nbr Type");
            foreach (var port in state.GetPorts())
            {
                var member = port.Mode == "trunk" ? SwitchState.PortAllowsVlan(port, vlan.Id) : port.AccessVlan == vlan.Id;
                if (!member) continue;
                var status = port.AdministrativelyDown ? "BLK" : "FWD";
                output.AppendLine($"{ShortPort(port.Name),-19} Desg {status,-3} 19        128.1    P2p ");
            }
        }
        return output.ToString().TrimEnd();
    }

    private string UpdatePort(Action<PortState> update) => state.TryConfigurePort(_selectedPort!, update, out var error) ? string.Empty : error;

    private string Exit()
    {
        switch (_mode)
        {
            case CliMode.User: return string.Empty;
            case CliMode.Privileged: _mode = CliMode.User; break;
            case CliMode.GlobalConfiguration: _mode = CliMode.Privileged; break;
            case CliMode.InterfaceConfiguration:
            case CliMode.VlanConfiguration:
            case CliMode.LineConfiguration: _mode = CliMode.GlobalConfiguration; break;
        }
        _selectedPort = null;
        _selectedVlan = null;
        return string.Empty;
    }

    private void Save()
    {
        state.SaveStartupConfiguration(store.Save);
    }

    private string Help(string prefix = "")
    {
        var commands = _mode switch
        {
            CliMode.User => new[] { "enable", "exit", "help", "show", "show version", "show vlan brief", "?" },
            CliMode.Privileged => new[] { "configure terminal", "copy running-config startup-config", "disable", "exit", "show", "show interfaces status", "show interfaces trunk", "show running-config", "show spanning-tree", "show startup-config", "show vlan brief", "show version", "write memory", "?" },
            CliMode.GlobalConfiguration => new[] { "banner motd", "end", "exit", "hostname", "interface", "line vty", "no banner motd", "no vlan", "vlan", "?" },
            CliMode.InterfaceConfiguration => new[] { "description", "end", "exit", "no shutdown", "shutdown", "spanning-tree portfast", "switchport access vlan", "switchport mode access", "switchport mode trunk", "switchport trunk allowed vlan", "?" },
            CliMode.VlanConfiguration => new[] { "end", "exit", "name", "?" },
            CliMode.LineConfiguration => new[] { "end", "exit", "login", "no login", "no password", "password", "?" },
            _ => Array.Empty<string>()
        };
        return string.Join("\n", commands.Where(command => command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool Is(string value, string full, string abbreviation) => value.Equals(full, StringComparison.OrdinalIgnoreCase) || value.Equals(abbreviation, StringComparison.OrdinalIgnoreCase);

    private static List<string> Tokenize(string line)
    {
        var matches = Regex.Matches(line, "\\\"([^\\\"]*)\\\"|(\\S+)");
        return matches.Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).ToList();
    }

    private static string ShortPort(string name) => name.StartsWith("FastEthernet", StringComparison.Ordinal) ? name.Replace("FastEthernet", "Fa", StringComparison.Ordinal) : name.Replace("GigabitEthernet", "Gi", StringComparison.Ordinal);

    private static bool TryParseVlanList(string input, out List<int> vlans)
    {
        vlans = new();
        foreach (var part in input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 1 && int.TryParse(range[0], out var single) && single is >= 1 and <= 4094) vlans.Add(single);
            else if (range.Length == 2 && int.TryParse(range[0], out var start) && int.TryParse(range[1], out var end) && start >= 1 && end <= 4094 && start <= end)
                vlans.AddRange(Enumerable.Range(start, end - start + 1));
            else return false;
        }
        vlans = vlans.Distinct().Order().ToList();
        return vlans.Count > 0;
    }
}
