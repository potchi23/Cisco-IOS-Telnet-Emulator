using System.Net.Sockets;
using System.Text;

namespace SwitchTelnetEmulator.Networking;

public sealed class TelnetConnection
{
    private readonly NetworkStream stream;
    public TelnetConnection(NetworkStream stream)
    {
        this.stream = stream;
    }
    private const byte Iac = 255;
    private const byte Dont = 254;
    private const byte Do = 253;
    private const byte Wont = 252;
    private const byte Will = 251;
    private const byte Subnegotiation = 250;
    private const byte SubnegotiationEnd = 240;
    private const byte Echo = 1;
    private const byte SuppressGoAhead = 3;
    private bool _skipNextLineTerminator;
    private readonly HashSet<byte> _localOptions = new() { Echo, SuppressGoAhead };
    private readonly HashSet<byte> _remoteOptions = new() { SuppressGoAhead };
    private readonly HashSet<byte> _rejectedLocalOptions = new();
    private readonly HashSet<byte> _rejectedRemoteOptions = new();

    public async Task NegotiateAsync(CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { Iac, Will, Echo, Iac, Will, SuppressGoAhead, Iac, Do, SuppressGoAhead }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var bytes = Encoding.UTF8.GetBytes(normalized.Replace("\n", "\r\n", StringComparison.Ordinal));
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken, bool echo = true)
    {
        var line = new List<byte>();
        var one = new byte[1];
        var telnetState = 0;
        var telnetCommand = (byte)0;
        var subnegotiationIac = false;
        while (true)
        {
            var count = await stream.ReadAsync(one, cancellationToken);
            if (count == 0) return line.Count == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
            var value = one[0];

            if (telnetState == 1)
            {
                if (value is Do or Dont or Will or Wont) { telnetCommand = value; telnetState = 2; }
                else if (value == Subnegotiation) { telnetState = 3; subnegotiationIac = false; }
                else if (value == Iac) { line.Add(Iac); telnetState = 0; }
                else telnetState = 0;
                continue;
            }
            if (telnetState == 2)
            {
                await HandleOptionAsync(telnetCommand, value, cancellationToken);
                telnetState = 0;
                continue;
            }
            if (telnetState == 3)
            {
                if (subnegotiationIac && value == SubnegotiationEnd) telnetState = 0;
                subnegotiationIac = value == Iac;
                continue;
            }
            if (value == Iac) { telnetState = 1; continue; }
            if (_skipNextLineTerminator)
            {
                _skipNextLineTerminator = false;
                if (value is 0 or 10) continue;
            }
            if (value == 13)
            {
                _skipNextLineTerminator = true;
                if (echo) await WriteAsync("\n", cancellationToken);
                return Encoding.UTF8.GetString(line.ToArray());
            }
            if (value == 10)
            {
                if (echo) await WriteAsync("\n", cancellationToken);
                return Encoding.UTF8.GetString(line.ToArray());
            }
            if (value is 8 or 127)
            {
                if (line.Count > 0)
                {
                    line.RemoveAt(line.Count - 1);
                    if (echo) await WriteAsync("\b \b", cancellationToken);
                }
                continue;
            }
            if (value < 32 && value != 9) continue;
            if (line.Count >= 4096) continue;
            line.Add(value);
            if (echo) await stream.WriteAsync(new[] { value }, cancellationToken);
        }
    }

    private async Task HandleOptionAsync(byte command, byte option, CancellationToken cancellationToken)
    {
        byte? replyCommand = command switch
        {
            Do => _localOptions.Contains(option) ? null : _rejectedLocalOptions.Add(option) ? Wont : null,
            Dont => _localOptions.Remove(option) ? Wont : null,
            Will => _remoteOptions.Contains(option) ? null : _rejectedRemoteOptions.Add(option) ? Dont : null,
            Wont => _remoteOptions.Remove(option) ? Dont : null,
            _ => null
        };
        if (replyCommand is null) return;
        await stream.WriteAsync(new byte[] { Iac, replyCommand.Value, option }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
