using SwitchTelnetEmulator.Cli;
using SwitchTelnetEmulator.Domain;
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

void Contains(string text, string expected)
{
    if (!text.Contains(expected, StringComparison.Ordinal)) failures.Add($"expected output to contain '{expected}', actual: {text}");
}

void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) failures.Add($"{label}: expected '{expected}', actual '{actual}'");
}
