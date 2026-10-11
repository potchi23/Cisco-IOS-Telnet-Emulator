using System.Text;

namespace SwitchTelnetEmulator.Domain;

public sealed class SwitchState
{
    private readonly object _sync = new();
    private SwitchConfiguration _running;
    private SwitchConfiguration _startup;

    public SwitchState(SwitchConfiguration? initial = null)
    {
        _running = initial?.Clone() ?? SwitchConfiguration.CreateDefault();
        _startup = _running.Clone();
    }

    public string Hostname { get { lock (_sync) return _running.Hostname; } }

    public IReadOnlyList<VlanState> GetVlans()
    {
        lock (_sync) return _running.Vlans.Values.OrderBy(v => v.Id).Select(v => v.Clone()).ToArray();
    }

    public IReadOnlyList<PortState> GetPorts()
    {
        lock (_sync) return _running.Ports.Values
            .OrderBy(p => p.Name.StartsWith("GigabitEthernet", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(p => int.Parse(p.Name[(p.Name.LastIndexOf('/') + 1)..]))
            .Select(p => p.Clone()).ToArray();
    }

    public SwitchConfiguration GetRunningConfiguration()
    {
        lock (_sync) return _running.Clone();
    }

    public SwitchConfiguration GetStartupConfiguration()
    {
        lock (_sync) return _startup.Clone();
    }

    public SwitchConfiguration.VtyAccessPolicy GetVtyAccessPolicy()
    {
        lock (_sync) return new SwitchConfiguration.VtyAccessPolicy(_running.Motd, _running.Vty.LoginEnabled, _running.Vty.Password);
    }

    public bool TrySetVtyPassword(string password, out string error)
    {
        if (password.Length is < 1 or > 128 || password.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            error = "% Password must be 1-128 non-whitespace characters.";
            return false;
        }

        lock (_sync) _running.Vty.Password = password;
        error = string.Empty;
        return true;
    }

    public void SetVtyLogin(bool enabled)
    {
        lock (_sync) _running.Vty.LoginEnabled = enabled;
    }

    public void ClearVtyPassword()
    {
        lock (_sync) _running.Vty.Password = null;
    }

    public bool TrySetMotd(string value, out string error)
    {
        if (value.Length > 4096 || value.Any(c => char.IsControl(c) && c is not ((char)13 or (char)10 or (char)9)))
        {
            error = "% MOTD must be at most 4096 characters and contain no control characters.";
            return false;
        }

        lock (_sync) _running.Motd = value;
        error = string.Empty;
        return true;
    }

    public bool TrySetHostname(string value, out string error)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 32 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            error = "% Invalid hostname (use 1-32 letters, digits, '-' or '_').";
            return false;
        }

        lock (_sync) _running.Hostname = value;
        error = string.Empty;
        return true;
    }

    public bool TryCreateVlan(int id, out string error)
    {
        if (id is < 1 or > 4094)
        {
            error = "% VLAN ID must be between 1 and 4094.";
            return false;
        }

        lock (_sync)
        {
            if (_running.Vlans.ContainsKey(id))
            {
                error = $"% VLAN {id} already exists.";
                return false;
            }
            _running.Vlans[id] = new VlanState(id, $"VLAN{id}");
        }
        error = string.Empty;
        return true;
    }

    public bool TryDeleteVlan(int id, out string error)
    {
        if (id == 1)
        {
            error = "% VLAN 1 cannot be deleted.";
            return false;
        }

        lock (_sync)
        {
            if (!_running.Vlans.ContainsKey(id))
            {
                error = $"% VLAN {id} does not exist.";
                return false;
            }
            if (_running.Ports.Values.Any(port => port.Mode == "access" && port.AccessVlan == id))
            {
                error = $"% VLAN {id} is assigned to an access port.";
                return false;
            }
            _running.Vlans.Remove(id);
            foreach (var port in _running.Ports.Values) port.AllowedVlans.Remove(id);
        }
        error = string.Empty;
        return true;
    }

    public bool TrySetVlanName(int id, string name, out string error)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 32 || name.Any(char.IsControl))
        {
            error = "% VLAN name must be 1-32 printable characters.";
            return false;
        }
        lock (_sync)
        {
            if (!_running.Vlans.TryGetValue(id, out var vlan))
            {
                error = $"% VLAN {id} does not exist.";
                return false;
            }
            vlan.Name = name;
        }
        error = string.Empty;
        return true;
    }

    public bool TryGetPort(string name, out PortState port)
    {
        var canonical = CanonicalPortName(name);
        lock (_sync)
        {
            if (canonical is not null && _running.Ports.TryGetValue(canonical, out var found))
            {
                port = found.Clone();
                return true;
            }
        }
        port = null!;
        return false;
    }

    public bool PortExists(string name) => TryGetPort(name, out _);

    public bool TryConfigurePort(string name, Action<PortState> update, out string error)
    {
        var canonical = CanonicalPortName(name);
        lock (_sync)
        {
            if (canonical is null || !_running.Ports.TryGetValue(canonical, out var port))
            {
                error = $"% Invalid interface '{name}'.";
                return false;
            }
            var updated = port.Clone();
            update(updated);
            if (updated.Mode is not ("access" or "trunk") || updated.Description.Length > 80 || updated.AllowedVlans.Any(id => id is < 1 or > 4094))
            {
                error = "% Invalid interface configuration.";
                return false;
            }
            port.Mode = updated.Mode;
            port.AccessVlan = updated.AccessVlan;
            port.AllowedVlans = updated.AllowedVlans;
            port.Description = updated.Description;
            port.AdministrativelyDown = updated.AdministrativelyDown;
            port.PortFast = updated.PortFast;
        }
        error = string.Empty;
        return true;
    }



    public bool TrySetAccessVlan(string name, int vlanId, out string error)
    {
        lock (_sync)
        {
            if (!_running.Vlans.ContainsKey(vlanId))
            {
                error = $"% VLAN {vlanId} does not exist. Create it first.";
                return false;
            }
            var canonical = CanonicalPortName(name);
            if (canonical is null || !_running.Ports.TryGetValue(canonical, out var port))
            {
                error = $"% Invalid interface '{name}'.";
                return false;
            }
            port.AccessVlan = vlanId;
        }
        error = string.Empty;
        return true;
    }

    public bool VlanExists(int vlanId)
    {
        lock (_sync) return _running.Vlans.ContainsKey(vlanId);
    }

    public bool TrySetAllowedVlans(string name, IEnumerable<int> vlanIds, out string error)
    {
        var canonical = CanonicalPortName(name);
        var values = vlanIds.Distinct().Order().ToArray();
        lock (_sync)
        {
            if (canonical is null || !_running.Ports.TryGetValue(canonical, out var port))
            {
                error = $"% Invalid interface '{name}'.";
                return false;
            }
            var missing = values.FirstOrDefault(id => !_running.Vlans.ContainsKey(id));
            if (values.Any(id => id is < 1 or > 4094))
            {
                error = "% VLAN IDs must be between 1 and 4094.";
                return false;
            }
            if (missing != 0)
            {
                error = $"% VLAN {missing} does not exist.";
                return false;
            }
            port.AllowedVlans = new SortedSet<int>(values);
        }
        error = string.Empty;
        return true;
    }

    public static bool PortAllowsVlan(PortState port, int vlanId) => port.AllowedVlans.Count == 0 || port.AllowedVlans.Contains(vlanId);

    public static string FormatAllowedVlans(PortState port) => port.AllowedVlans.Count == 0 ? "all" : string.Join(',', port.AllowedVlans);

    public void SaveStartupConfiguration(Action<SwitchConfiguration> persist)
    {
        lock (_sync)
        {
            var snapshot = _running.Clone();
            persist(snapshot);
            _startup = snapshot;
        }
    }

    public void LoadStartupConfiguration(SwitchConfiguration configuration)
    {
        lock (_sync)
        {
            _running = configuration.Clone();
            _startup = configuration.Clone();
        }
    }

    public static string? CanonicalPortName(string value)
    {
        var token = value.Trim();
        var lower = token.ToLowerInvariant();
        var slash = lower.IndexOf('/');
        if (slash < 0) return null;
        var prefix = lower[..slash];
        var suffix = lower[(slash + 1)..];
        if (!int.TryParse(suffix, out var port)) return null;
        if (prefix is "fastethernet0" or "fa0" or "fastethernet")
            return port is >= 1 and <= 24 ? $"FastEthernet0/{port}" : null;
        if (prefix is "gigabitethernet0" or "gi0" or "gigabitethernet")
            return port is >= 1 and <= 2 ? $"GigabitEthernet0/{port}" : null;
        return null;
    }

    public static string RenderConfiguration(SwitchConfiguration configuration)
    {
        var output = new StringBuilder();
        output.AppendLine($"hostname {configuration.Hostname}");
        if (!string.IsNullOrEmpty(configuration.Motd))
        {
            var delimiter = Enumerable.Range(33, 94).Concat(Enumerable.Range(161, 0xD7FF - 160))
                .Select(value => (char)value)
                .First(candidate => !configuration.Motd.Contains(candidate));
            output.AppendLine($"banner motd {delimiter}");
            output.Append(configuration.Motd);
            output.AppendLine();
            output.AppendLine(delimiter.ToString());
        }
        output.AppendLine("!");
        output.AppendLine("line vty 0 4");
        if (!string.IsNullOrEmpty(configuration.Vty.Password)) output.AppendLine($" password {configuration.Vty.Password}");
        output.AppendLine(configuration.Vty.LoginEnabled ? " login" : " no login");
        output.AppendLine("!");
        output.AppendLine("!");
        foreach (var vlan in configuration.Vlans.Values.OrderBy(v => v.Id))
        {
            if (vlan.Id == 1 && vlan.Name == "default") continue;
            output.AppendLine($"vlan {vlan.Id}");
            if (vlan.Name != $"VLAN{vlan.Id}") output.AppendLine($" name {vlan.Name}");
            output.AppendLine("!");
        }
        foreach (var port in configuration.Ports.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            output.AppendLine($"interface {port.Name}");
            if (!string.IsNullOrWhiteSpace(port.Description)) output.AppendLine($" description {port.Description}");
            output.AppendLine($" switchport mode {port.Mode}");
            if (port.Mode == "access") output.AppendLine($" switchport access vlan {port.AccessVlan}");
            else output.AppendLine($" switchport trunk allowed vlan {FormatAllowedVlans(port)}");
            if (port.PortFast) output.AppendLine(" spanning-tree portfast");
            output.AppendLine(port.AdministrativelyDown ? " shutdown" : " no shutdown");
            output.AppendLine("!");
        }
        return output.ToString().TrimEnd();
    }
}

public sealed class SwitchConfiguration
{
    public string Hostname { get; set; } = "Switch";
    public string Motd { get; set; } = string.Empty;
    public VtyConfiguration Vty { get; set; } = new();
    public Dictionary<int, VlanState> Vlans { get; set; } = new();

    public sealed class VtyConfiguration
    {
        public bool LoginEnabled { get; set; } = true;
        public string? Password { get; set; }

        public VtyConfiguration Clone() => new() { LoginEnabled = LoginEnabled, Password = Password };
    }

    public sealed record VtyAccessPolicy(string Motd, bool LoginEnabled, string? Password);
    public Dictionary<string, PortState> Ports { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static SwitchConfiguration CreateDefault()
    {
        var configuration = new SwitchConfiguration();
        configuration.Vlans[1] = new VlanState(1, "default");
        for (var i = 1; i <= 24; i++)
        {
            var name = $"FastEthernet0/{i}";
            configuration.Ports[name] = new PortState(name);
        }
        for (var i = 1; i <= 2; i++)
        {
            var name = $"GigabitEthernet0/{i}";
            configuration.Ports[name] = new PortState(name);
        }
        return configuration;
    }

    public SwitchConfiguration Clone() => new()
    {
        Hostname = Hostname,
        Motd = Motd,
        Vty = Vty.Clone(),
        Vlans = Vlans.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
        Ports = Ports.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase)
    };
}

public sealed class VlanState(int id, string name)
{
    public int Id { get; set; } = id;
    public string Name { get; set; } = name;
    public VlanState Clone() => new(Id, Name);
}

public sealed class PortState(string name)
{
    public string Name { get; set; } = name;
    public string Mode { get; set; } = "access";
    public int AccessVlan { get; set; } = 1;
    public SortedSet<int> AllowedVlans { get; set; } = new();
    public string Description { get; set; } = string.Empty;
    public bool AdministrativelyDown { get; set; }
    public bool PortFast { get; set; }

    public PortState Clone() => new(Name)
    {
        Mode = Mode,
        AccessVlan = AccessVlan,
        AllowedVlans = new SortedSet<int>(AllowedVlans),
        Description = Description,
        AdministrativelyDown = AdministrativelyDown,
        PortFast = PortFast
    };
}
