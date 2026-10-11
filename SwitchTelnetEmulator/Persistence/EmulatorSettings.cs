using System.Net;
using System.Text.Json;

namespace SwitchTelnetEmulator.Persistence;

public sealed class EmulatorSettings
{
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 2323;
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "cisco";

    public static (EmulatorSettings Settings, IPAddress Address) Load(string path)
    {
        EmulatorSettings settings;
        if (!File.Exists(path))
        {
            settings = new EmulatorSettings();
            Save(path, settings);
        }
        else
        {
            try
            {
                settings = JsonSerializer.Deserialize<EmulatorSettings>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("El archivo de configuración está vacío.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"No se pudo leer la configuración de escucha '{path}': {exception.Message}", exception);
            }
        }

        if (!IPAddress.TryParse(settings.ListenAddress, out var address))
            throw new InvalidDataException($"ListenAddress no es una dirección IP válida: '{settings.ListenAddress}'.");
        if (settings.Port is < 1 or > 65535)
            throw new InvalidDataException("Port debe estar entre 1 y 65535.");
        if (settings.Username is null || string.IsNullOrWhiteSpace(settings.Username) || settings.Username.Any(char.IsControl))
            throw new InvalidDataException("Username must not be empty or contain control characters.");
        if (settings.Password is null || string.IsNullOrEmpty(settings.Password) || settings.Password.Any(char.IsControl))
            throw new InvalidDataException("Password must not be empty or contain control characters.");

        return (settings, address);
    }

    private static void Save(string path, EmulatorSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }
}
