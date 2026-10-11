using System.Text.Json;
using SwitchTelnetEmulator.Domain;

namespace SwitchTelnetEmulator.Persistence;

public sealed class ConfigurationStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path = Path.GetFullPath(path);

    public SwitchConfiguration Load()
    {
        if (!File.Exists(_path)) return SwitchConfiguration.CreateDefault();
        try
        {
            var text = File.ReadAllText(_path);
            var configuration = JsonSerializer.Deserialize<SwitchConfiguration>(text, JsonOptions)
                ?? throw new InvalidDataException("Configuration file is empty.");
            Validate(configuration);
            return configuration;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException($"Invalid startup configuration at '{_path}': {exception.Message}", exception);
        }
    }

    public void Save(SwitchConfiguration configuration)
    {
        Validate(configuration);
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(configuration, JsonOptions));
            File.Move(temporary, _path, true);
        }
    }

    private static void Validate(SwitchConfiguration configuration)
    {
        if (configuration.Hostname is null || configuration.Hostname.Length is < 1 or > 32 || configuration.Vlans is null || configuration.Ports is null)
            throw new InvalidDataException("Invalid hostname, VLAN table, or port table.");
        if (!configuration.Vlans.ContainsKey(1) || configuration.Vlans.Any(pair => pair.Key is < 1 or > 4094 || pair.Value is null || pair.Value.Id != pair.Key))
            throw new InvalidDataException("VLAN table contains an invalid entry or is missing VLAN 1.");
        if (configuration.Ports.Count != 26 || configuration.Ports.Any(pair =>
                SwitchState.CanonicalPortName(pair.Key) is not { } name || name != pair.Key || pair.Value is null || pair.Value.Name != pair.Key ||
                pair.Value.Mode is not ("access" or "trunk") || !configuration.Vlans.ContainsKey(pair.Value.AccessVlan) ||
                pair.Value.AllowedVlans is null || pair.Value.AllowedVlans.Any(vlan => vlan is < 1 or > 4094)))
            throw new InvalidDataException("Port table contains an invalid interface configuration.");
    }
}
