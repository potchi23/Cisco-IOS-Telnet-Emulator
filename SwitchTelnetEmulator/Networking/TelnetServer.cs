using System.Net;
using System.Net.Sockets;
using SwitchTelnetEmulator.Cli;
using SwitchTelnetEmulator.Domain;
using SwitchTelnetEmulator.Persistence;

namespace SwitchTelnetEmulator.Networking;

public sealed class TelnetServer(IPAddress address, int port, SwitchState state, ConfigurationStore store, string username, string password)
{
    public int BoundPort { get; private set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(address, port);
        listener.Start();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"Switch emulator listening on {address}:{BoundPort}");
        Started.TrySetResult();
        Console.WriteLine("Telnet is unencrypted. Keep the listener on loopback unless you trust the network.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { listener.Stop(); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            Console.WriteLine($"Telnet client connected: {remote}");
            var telnet = new TelnetConnection(client.GetStream());
            try
            {
                await telnet.NegotiateAsync(cancellationToken);
                var session = new CliSession(state, store);
                var access = state.GetVtyAccessPolicy();
                if (!string.IsNullOrEmpty(access.Motd)) await telnet.WriteAsync("\n" + access.Motd + "\n", cancellationToken);
                if (access.LoginEnabled)
                {
                    var effectivePassword = access.Password ?? password;
                    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(effectivePassword))
                    {
                        await telnet.WriteAsync("\nVTY login is enabled but no credentials are configured.\n", cancellationToken);
                        return;
                    }
                    var authenticated = false;
                    for (var attempt = 0; attempt < 3 && !authenticated; attempt++)
                    {
                        await telnet.WriteAsync("Username: ", cancellationToken);
                        var suppliedUsername = await telnet.ReadLineAsync(cancellationToken);
                        if (suppliedUsername is null) return;
                        await telnet.WriteAsync("Password: ", cancellationToken);
                        var suppliedPassword = await telnet.ReadLineAsync(cancellationToken, echo: false);
                        if (suppliedPassword is null) return;
                        authenticated = string.Equals(suppliedUsername, username, StringComparison.Ordinal) &&
                            string.Equals(suppliedPassword, effectivePassword, StringComparison.Ordinal);
                        if (!authenticated) await telnet.WriteAsync("\n% Login invalid\n", cancellationToken);
                    }
                    if (!authenticated) return;
                    await telnet.WriteAsync("\n", cancellationToken);
                }
                await telnet.WriteAsync("SwitchTelnetEmulator - educational Layer 2 switch\nType ? for help.\n" + session.Prompt + " ", cancellationToken);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var command = await telnet.ReadLineAsync(cancellationToken);
                    if (command is null) break;
                    try
                    {
                        var result = session.Execute(command);
                        if (result.Length > 0) await telnet.WriteAsync(result + "\n", cancellationToken);
                    }
                    catch (IOException exception)
                    {
                        await telnet.WriteAsync($"% Unable to save configuration: {exception.Message}\n", cancellationToken);
                    }
                    await telnet.WriteAsync(session.Prompt + " ", cancellationToken);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            finally { Console.WriteLine($"Telnet client disconnected: {remote}"); }
        }
    }
}
