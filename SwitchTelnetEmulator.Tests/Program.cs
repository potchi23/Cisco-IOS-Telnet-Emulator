using System.Net;
using System.Net.Sockets;
using System.Text;
using SwitchTelnetEmulator.Cli;
using SwitchTelnetEmulator.Domain;
using SwitchTelnetEmulator.Networking;
using SwitchTelnetEmulator.Persistence;

var failures = new List<string>();
var temporaryRoot = Path.Combine(Path.GetTempPath(), "switch-emulator-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
try
{
    TestCliModesAndVlan();
    TestTrunkConfiguration();
    TestStartupPersistence(temporaryRoot);
    TestVlanDeletionSafety();
    TestVtyAndMotd(temporaryRoot);
    await TestTelnetAuthentication(temporaryRoot);
    TestListenerSettings(temporaryRoot);
}
finally
{
    Directory.Delete(temporaryRoot, true);
}

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    Environment.ExitCode = 1;
}
else Console.WriteLine("All emulator tests passed.");

void TestCliModesAndVlan()
{
    var state = new SwitchState();
    var session = new CliSession(state, new ConfigurationStore(Path.Combine(temporaryRoot, "modes.json")));
    Equal("Switch>", session.Prompt, "initial prompt");
    session.Execute("enable");
    Equal("Switch#", session.Prompt, "enable mode");
    session.Execute("conf t");
    session.Execute("hostname Lab");
    session.Execute("vlan 20");
    session.Execute("name QA");
    session.Execute("exit");
    session.Execute("int Fa0/1");
    session.Execute("switchport access vlan 20");
    session.Execute("end");
    Contains(session.Execute("show vlan brief"), "20   QA");
    Contains(session.Execute("show vlan brief"), "Fa0/1");
    Equal("Lab#", session.Prompt, "hostname reflected in prompt");
}

void TestTrunkConfiguration()
{
    var state = new SwitchState();
    var session = new CliSession(state, new ConfigurationStore(Path.Combine(temporaryRoot, "trunk.json")));
    session.Execute("enable");
    session.Execute("configure terminal");
    session.Execute("vlan 10");
    session.Execute("exit");
    session.Execute("vlan 20");
    session.Execute("exit");
    session.Execute("interface Gi0/1");
    session.Execute("switchport mode trunk");
    session.Execute("switchport trunk allowed vlan 10,20");
    session.Execute("end");
    Contains(session.Execute("show interfaces trunk"), "10,20");
    Contains(session.Execute("show running-config"), "switchport trunk allowed vlan 10,20");
}

void TestStartupPersistence(string root)
{
    var path = Path.Combine(root, "persist", "startup.json");
    var store = new ConfigurationStore(path);
    var state = new SwitchState(store.Load());
    var session = new CliSession(state, store);
    session.Execute("enable");
    session.Execute("configure terminal");
    session.Execute("hostname SavedSwitch");
    session.Execute("vlan 30");
    session.Execute("exit");
    session.Execute("end");
    Contains(session.Execute("show startup-config"), "hostname Switch");
    session.Execute("write memory");
    Contains(session.Execute("show startup-config"), "hostname SavedSwitch");
    var reloaded = new ConfigurationStore(path).Load();
    Equal("SavedSwitch", reloaded.Hostname, "persisted hostname");
    if (!reloaded.Vlans.ContainsKey(30)) failures.Add("persisted VLAN 30");
}

void TestListenerSettings(string root)
{
    var path = Path.Combine(root, "settings", "emulator-settings.json");
    var (settings, address) = EmulatorSettings.Load(path);
    Equal("0.0.0.0", settings.ListenAddress, "default listen address");
    Equal(2323, settings.Port, "default listen port");
    Equal("0.0.0.0", address.ToString(), "parsed listen address");
    Equal("admin", settings.Username, "default username");
    Equal("cisco", settings.Password, "default password");
    File.WriteAllText(path, "{\"ListenAddress\":\"not-an-ip\",\"Port\":2323}");
    try
    {
        EmulatorSettings.Load(path);
        failures.Add("invalid listener address should be rejected");
    }
    catch (InvalidDataException) { }
}

void TestVlanDeletionSafety()
{
    var state = new SwitchState();
    var session = new CliSession(state, new ConfigurationStore(Path.Combine(temporaryRoot, "delete.json")));
    session.Execute("enable");
    session.Execute("configure terminal");
    session.Execute("vlan 40");
    session.Execute("exit");
    session.Execute("interface Fa0/1");
    session.Execute("switchport access vlan 40");
    session.Execute("end");
    session.Execute("configure terminal");
    Contains(session.Execute("no vlan 40"), "assigned to an access port");
}

void TestVtyAndMotd(string root)
{
    var path = Path.Combine(root, "vty", "startup.json");
    var store = new ConfigurationStore(path);
    var state = new SwitchState(store.Load());
    var session = new CliSession(state, store);
    session.Execute("enable");
    session.Execute("configure terminal");
    Equal(string.Empty, session.Execute("line vty 0"), "enter first VTY line");
    Equal("Switch(config-line)#", session.Prompt, "first VTY line prompt");
    session.Execute("exit");
    Equal(string.Empty, session.Execute("line vty 0 4"), "enter VTY line mode");
    Equal("Switch(config-line)#", session.Prompt, "VTY line prompt");
    session.Execute("password changed-secret");
    session.Execute("login");
    Equal(string.Empty, session.Execute("exit"), "exit VTY line mode");
    Equal("Switch(config)#", session.Prompt, "return to global config");
    Contains(session.Execute("line vty 0 5"), "Only line vty");
    Equal("Enter TEXT message. End with the delimiter.", session.Execute("banner motd #Welcome"), "begin multiline MOTD");
    Equal(string.Empty, session.Execute("to the lab"), "capture MOTD line");
    Equal(string.Empty, session.Execute("#"), "finish MOTD");
    session.Execute("end");
    Contains(session.Execute("show running-config"), "password changed-secret");
    var runningConfig = session.Execute("show running-config");
    Contains(runningConfig.Replace("\r\n", "\n", StringComparison.Ordinal), "banner motd !\nWelcome\nto the lab\n!");
    Equal("Welcome\nto the lab", state.GetVtyAccessPolicy().Motd, "MOTD content");
    session.Execute("write memory");
    var reloaded = new ConfigurationStore(path).Load();
    Equal("changed-secret", reloaded.Vty.Password, "persisted VTY password");
    Equal(true, reloaded.Vty.LoginEnabled, "persisted VTY login");
    Equal("Welcome\nto the lab", reloaded.Motd, "persisted MOTD");

    var legacyPath = Path.Combine(root, "legacy.json");
    var legacy = SwitchConfiguration.CreateDefault();
    var json = System.Text.Json.JsonSerializer.Serialize(legacy);
    json = System.Text.RegularExpressions.Regex.Replace(json, "\\\"Motd\\\":\\s*\\\"\\\",?", string.Empty);
    json = System.Text.RegularExpressions.Regex.Replace(json, "\\\"Vty\\\":\\s*\\{[^}]*\\},?", string.Empty);
    File.WriteAllText(legacyPath, json);
    var legacyLoaded = new ConfigurationStore(legacyPath).Load();
    Equal(true, legacyLoaded.Vty.LoginEnabled, "legacy VTY login default");
    Equal(string.Empty, legacyLoaded.Motd, "legacy MOTD default");
    var legacySettingsPath = Path.Combine(root, "legacy-settings.json");
    File.WriteAllText(legacySettingsPath, "{\"ListenAddress\":\"127.0.0.1\",\"Port\":2323}");
    var (legacySettings, _) = EmulatorSettings.Load(legacySettingsPath);
    Equal("admin", legacySettings.Username, "legacy settings username default");
    Equal("cisco", legacySettings.Password, "legacy settings password default");
}

async Task TestTelnetAuthentication(string root)
{
    var state = new SwitchState();
    state.TrySetMotd("Test banner", out _);
    state.TrySetVtyPassword("secret", out _);
    var server = new TelnetServer(IPAddress.Loopback, 0, state, new ConfigurationStore(Path.Combine(root, "telnet.json")), "operator", "initial");
    using var shutdown = new CancellationTokenSource();
    var runTask = server.RunAsync(shutdown.Token);
    await server.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
    using (var rejected = new TcpClient())
    {
        await rejected.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        var rejectedStream = rejected.GetStream();
        var firstPrompt = await ReadUntil(rejectedStream, "Username: ");
        Contains(firstPrompt, "Test banner");
        Contains(firstPrompt, "Username: ");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await ReadUntil(rejectedStream, "Username: ");
            await rejectedStream.WriteAsync(Encoding.UTF8.GetBytes("operator\r\n"));
            await ReadUntil(rejectedStream, "Password: ");
            await rejectedStream.WriteAsync(Encoding.UTF8.GetBytes("wrong\r\n"));
        }
        var denied = await ReadUntil(rejectedStream, "% Login invalid");
        if (denied.Contains("SwitchTelnetEmulator - educational", StringComparison.Ordinal) || denied.Contains("Switch> ", StringComparison.Ordinal))
            failures.Add("failed authentication must not reach the CLI");
        var buffer = new byte[256];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int remaining;
        try
        {
            do
            {
                remaining = await rejectedStream.ReadAsync(buffer, timeout.Token);
                if (remaining > 0 && Encoding.UTF8.GetString(buffer, 0, remaining).Contains("SwitchTelnetEmulator", StringComparison.Ordinal))
                    failures.Add("failed authentication must not receive CLI welcome");
            } while (remaining > 0);
        }
        catch (IOException) { } // A TCP reset also confirms the rejected client was disconnected.
    }

    using (var probe = new TcpClient())
    {
        await probe.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        var stream = probe.GetStream();
        var initial = await ReadUntil(stream, "Username: ");
        Contains(initial, "Test banner");
        if (initial.Contains("SwitchTelnetEmulator - educational", StringComparison.Ordinal))
            failures.Add("CLI welcome must wait until authentication succeeds");
        await stream.WriteAsync(Encoding.UTF8.GetBytes("operator\r\n"));
        var passwordPrompt = await ReadUntil(stream, "Password: ");
        Contains(passwordPrompt, "operator");
        await stream.WriteAsync(Encoding.UTF8.GetBytes("secret\r\n"));
        var cli = await ReadUntil(stream, "Switch> ");
        Contains(cli, "SwitchTelnetEmulator - educational");
        if (cli.Contains("secret", StringComparison.Ordinal)) failures.Add("password must not be echoed");
    }

    state.ClearVtyPassword();
    using (var settingsCredentials = new TcpClient())
    {
        await settingsCredentials.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        var stream = settingsCredentials.GetStream();
        await ReadUntil(stream, "Username: ");
        await stream.WriteAsync(Encoding.UTF8.GetBytes("operator\r\n"));
        await ReadUntil(stream, "Password: ");
        await stream.WriteAsync(Encoding.UTF8.GetBytes("initial\r\n"));
        Contains(await ReadUntil(stream, "Switch> "), "SwitchTelnetEmulator - educational");
    }

    state.SetVtyLogin(false);
    using (var noLogin = new TcpClient())
    {
        await noLogin.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        var greeting = await ReadUntil(noLogin.GetStream(), "Switch> ");
        if (greeting.Contains("Username:", StringComparison.Ordinal)) failures.Add("no login must bypass authentication");
    }
    shutdown.Cancel();
    await runTask;
}

static async Task<string> ReadUntil(NetworkStream stream, string marker)
{
    var output = new StringBuilder();
    var buffer = new byte[1];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!output.ToString().Contains(marker, StringComparison.Ordinal))
    {
        var count = await stream.ReadAsync(buffer, timeout.Token);
        if (count == 0) break;
        output.Append((char)buffer[0]);
    }
    return output.ToString();
}

void Contains(string text, string expected)
{
    if (!text.Contains(expected, StringComparison.Ordinal)) failures.Add($"expected output to contain '{expected}', actual: {text}");
}

void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) failures.Add($"{label}: expected '{expected}', actual '{actual}'");
}
