using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Config;

namespace NetWatch.Collectors.Receivers;

/// <summary>
/// Turns received syslog text into stored messages and alert signals: parses, maps the source address to a
/// device, applies the rate limit and the unknown-source policy. Used by the UDP/TCP listeners and the simulator.
/// </summary>
public sealed class SyslogPipeline
{
    private readonly InventoryCache _inventory;
    private readonly EventIngestService _ingest;
    private readonly IAlertSignalSink _alerts;
    private readonly SettingsService _settings;
    private readonly ReceiverStats _stats;
    private readonly TimeProvider _time;

    public SyslogPipeline(InventoryCache inventory, EventIngestService ingest, IAlertSignalSink alerts, SettingsService settings, ReceiverStats stats, TimeProvider time)
    {
        _inventory = inventory;
        _ingest = ingest;
        _alerts = alerts;
        _settings = settings;
        _stats = stats;
        _time = time;
    }

    public void Accept(IPAddress source, string text)
    {
        _stats.Syslog();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var device = _inventory.FindByIp(source);
        if (device is null)
        {
            _stats.Unknown();
            if (!_settings.Receivers.AcceptUnknownSources)
            {
                _stats.Drop();
                return;
            }
        }

        if (!_ingest.Admit())
        {
            return;
        }

        ParsedSyslog parsed;
        try
        {
            parsed = SyslogParser.Parse(text);
        }
        catch (Exception)
        {
            _stats.ParseError();
            parsed = new ParsedSyslog(1, 5, null, null, null, null, null, text.Length > 8192 ? text[..8192] : text);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        _ingest.Enqueue(new SyslogMessage
        {
            ReceivedUtc = now,
            MessageUtc = parsed.MessageUtc,
            SourceIp = (source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source).ToString(),
            DeviceId = device?.Id,
            Facility = parsed.Facility,
            SyslogSeverity = parsed.Severity,
            Hostname = parsed.Hostname,
            AppName = parsed.AppName,
            ProcId = parsed.ProcId,
            MsgId = parsed.MsgId,
            Message = parsed.Message,
        });

        if (device is not null)
        {
            var prefix = parsed.AppName is null ? string.Empty : parsed.AppName + ": ";
            _alerts.Publish(new EventSignal(device.Id, EventKeys.SyslogMessage, $"{prefix}{parsed.Message}", now,
                SeverityMapping.FromSyslog(parsed.Severity), parsed.AppName));
        }
    }
}

/// <summary>Syslog listeners: UDP and TCP (octet-counted or newline-framed, RFC 6587). Ports from the "receivers" settings.</summary>
public sealed class SyslogReceiver : BackgroundService
{
    private const int MaxMessageBytes = 64 * 1024;
    private const int MaxTcpConnections = 500;

    private readonly SyslogPipeline _pipeline;
    private readonly SettingsService _settings;
    private readonly ReceiverStats _stats;
    private readonly ILogger<SyslogReceiver> _logger;
    private int _connections;

    public SyslogReceiver(SyslogPipeline pipeline, SettingsService settings, ReceiverStats stats, ILogger<SyslogReceiver> logger)
    {
        _pipeline = pipeline;
        _settings = settings;
        _stats = stats;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var s = _settings.Receivers;
        if (!s.SyslogEnabled)
        {
            _stats.SetListener("syslog", "disabled");
            return Task.CompletedTask;
        }

        var tasks = new List<Task> { Task.Run(() => UdpLoopAsync(s.SyslogUdpPort, stoppingToken), CancellationToken.None) };
        if (s.SyslogTcpEnabled)
        {
            tasks.Add(Task.Run(() => TcpLoopAsync(s.SyslogTcpPort, stoppingToken), CancellationToken.None));
        }

        return Task.WhenAll(tasks);
    }

    private async Task UdpLoopAsync(int port, CancellationToken ct)
    {
        var name = $"syslog/udp:{port}";
        UdpClient udp;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetworkV6);
            udp.Client.DualMode = true;
            udp.Client.ReceiveBufferSize = 8 * 1024 * 1024;
            udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            _stats.SetListener(name, "listening");
            _logger.LogInformation("Syslog receiver listening on UDP {Port}", port);
        }
        catch (SocketException ex)
        {
            _stats.SetListener(name, $"error: {ex.Message}");
            _logger.LogError(ex, "Cannot listen for syslog on UDP {Port}", port);
            return;
        }

        using (udp)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var r = await udp.ReceiveAsync(ct);
                    _pipeline.Accept(r.RemoteEndPoint.Address, Encoding.UTF8.GetString(r.Buffer));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    // ICMP port unreachable etc. on Windows surfaces as ConnectionReset: keep listening.
                    _logger.LogDebug(ex, "Syslog UDP receive error");
                }
            }
        }
    }

    private async Task TcpLoopAsync(int port, CancellationToken ct)
    {
        var name = $"syslog/tcp:{port}";
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            listener.Start(100);
            _stats.SetListener(name, "listening");
            _logger.LogInformation("Syslog receiver listening on TCP {Port}", port);
        }
        catch (SocketException ex)
        {
            _stats.SetListener(name, $"error: {ex.Message}");
            _logger.LogError(ex, "Cannot listen for syslog on TCP {Port}", port);
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                if (Interlocked.Increment(ref _connections) > MaxTcpConnections)
                {
                    Interlocked.Decrement(ref _connections);
                    client.Dispose();
                    continue;
                }

                _ = Task.Run(() => HandleTcpAsync(client, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleTcpAsync(TcpClient client, CancellationToken ct)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[MaxMessageBytes];
                var pending = new MemoryStream();
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromMinutes(10));
                    var read = await stream.ReadAsync(buffer, idle.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    pending.Write(buffer, 0, read);
                    foreach (var message in TcpFraming.Extract(pending, MaxMessageBytes))
                    {
                        _pipeline.Accept(remote, message);
                    }
                }

                // A final message without a trailing newline.
                if (pending.Length > 0)
                {
                    _pipeline.Accept(remote, Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Connection closed or idle timeout.
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
        }
    }
}

/// <summary>RFC 6587 framing: octet counting ("LEN SP MSG") or non-transparent (LF-terminated) messages.</summary>
public static class TcpFraming
{
    /// <summary>Removes complete messages from <paramref name="buffer"/> and returns them; incomplete data stays.</summary>
    public static List<string> Extract(MemoryStream buffer, int maxMessageBytes)
    {
        var messages = new List<string>();
        var data = buffer.GetBuffer();
        var length = (int)buffer.Length;
        var pos = 0;

        while (pos < length)
        {
            if (char.IsAsciiDigit((char)data[pos]))
            {
                // Octet counting: digits, a space, then exactly LEN bytes.
                var space = Array.IndexOf(data, (byte)' ', pos, Math.Min(10, length - pos));
                if (space < 0)
                {
                    if (length - pos >= 10)
                    {
                        pos = SkipLine(data, pos, length, messages); // not a length prefix: treat as a line
                        continue;
                    }

                    break;
                }

                if (!int.TryParse(Encoding.ASCII.GetString(data, pos, space - pos), NumberStyles.None, CultureInfo.InvariantCulture, out var len) || len <= 0 || len > maxMessageBytes)
                {
                    pos = SkipLine(data, pos, length, messages);
                    continue;
                }

                if (space + 1 + len > length)
                {
                    break; // wait for the rest
                }

                messages.Add(Encoding.UTF8.GetString(data, space + 1, len));
                pos = space + 1 + len;
                continue;
            }

            var lf = Array.IndexOf(data, (byte)'\n', pos, length - pos);
            if (lf < 0)
            {
                if (length - pos > maxMessageBytes)
                {
                    messages.Add(Encoding.UTF8.GetString(data, pos, maxMessageBytes));
                    pos = length;
                }

                break;
            }

            if (lf > pos)
            {
                messages.Add(Encoding.UTF8.GetString(data, pos, lf - pos).TrimEnd('\r'));
            }

            pos = lf + 1;
        }

        // Keep the unconsumed tail.
        var tail = length - pos;
        if (tail > 0)
        {
            Buffer.BlockCopy(data, pos, data, 0, tail);
        }

        buffer.SetLength(tail);
        return messages.Where(m => m.Length > 0).ToList();
    }

    private static int SkipLine(byte[] data, int pos, int length, List<string> messages)
    {
        var lf = Array.IndexOf(data, (byte)'\n', pos, length - pos);
        var end = lf < 0 ? length : lf;
        messages.Add(Encoding.UTF8.GetString(data, pos, end - pos).TrimEnd('\r'));
        return lf < 0 ? length : lf + 1;
    }
}
