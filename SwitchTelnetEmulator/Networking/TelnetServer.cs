using System.Net;
using System.Net.Sockets;
using SwitchTelnetEmulator.Cli;
using SwitchTelnetEmulator.Domain;
using SwitchTelnetEmulator.Persistence;

namespace SwitchTelnetEmulator.Networking;

public sealed class TelnetServer(IPAddress address, int port, SwitchState state, ConfigurationStore store)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(address, port);
        listener.Start();
        Console.WriteLine($"Switch emulator listening on {address}:{port}");
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
                await telnet.WriteAsync("\nSwitchTelnetEmulator - educational Layer 2 switch\nType ? for help.\n" + session.Prompt + " ", cancellationToken);
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
