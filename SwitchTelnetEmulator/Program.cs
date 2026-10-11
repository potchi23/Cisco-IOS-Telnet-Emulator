using System.Net;
using System.Net.Sockets;
using SwitchTelnetEmulator.Domain;
using SwitchTelnetEmulator.Networking;
using SwitchTelnetEmulator.Persistence;

var configDirectory = AppContext.BaseDirectory;
var listenConfigPath = Path.Combine(configDirectory, "emulator-settings.json");
var configPath = Path.Combine(configDirectory, "data", "startup-config.json");
(IPAddress Address, int Port) listener;
EmulatorSettings settings;
try
{
    (settings, var address) = EmulatorSettings.Load(listenConfigPath);
    listener = (address, settings.Port);
}
catch (InvalidDataException exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
    return;
}

var options = ParseArguments(args, listener.Address, listener.Port, configPath);
if (options.ShowHelp)
{
    Console.WriteLine("SwitchTelnetEmulator [--host <IP>] [--port <1-65535>] [--config <path>]");
    return;
}

var store = new ConfigurationStore(options.ConfigPath);
SwitchConfiguration startup;
try
{
    startup = store.Load();
}
catch (InvalidDataException exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
    return;
}
var state = new SwitchState(startup);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await new TelnetServer(options.Address, options.Port, state, store, settings.Username, settings.Password).RunAsync(shutdown.Token);
}
catch (SocketException exception)
{
    Console.Error.WriteLine($"Unable to start listener: {exception.Message}");
    Environment.ExitCode = 1;
}

static (IPAddress Address, int Port, string ConfigPath, bool ShowHelp) ParseArguments(string[] arguments, IPAddress defaultAddress, int defaultPort, string defaultConfigPath)
{
    var address = defaultAddress;
    var port = defaultPort;
    var configPath = defaultConfigPath;
    var help = false;
    for (var index = 0; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "--help" or "-h": help = true; break;
            case "--host" when index + 1 < arguments.Length:
                if (!IPAddress.TryParse(arguments[++index], out address!)) throw new ArgumentException("--host must be an IP address.");
                break;
            case "--port" when index + 1 < arguments.Length:
                if (!int.TryParse(arguments[++index], out port) || port is < 1 or > 65535) throw new ArgumentException("--port must be between 1 and 65535.");
                break;
            case "--config" when index + 1 < arguments.Length: configPath = arguments[++index]; break;
            default: throw new ArgumentException($"Unknown or incomplete argument: {arguments[index]}");
        }
    }
    return (address, port, Path.GetFullPath(configPath), help);
}
