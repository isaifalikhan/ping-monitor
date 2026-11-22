using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;

namespace NetWatch.Alerts.Notifications;

/// <summary>Shared text formatting for notification channels.</summary>
internal static class NotificationText
{
    public static string Time(DateTime utc) =>
        LocalTime.ToLocal(utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " PKT";

    public static string Verb(NotificationKind kind) => kind switch
    {
        NotificationKind.Raised => "RAISED",
        NotificationKind.Resolved => "RESOLVED",
        NotificationKind.Escalated => "ESCALATED",
        _ => "TEST",
    };

    public static string Subject(AlertNotification n) =>
        $"[NetWatch] {Verb(n.Kind)} {n.Alert.Severity}: {n.Alert.Title}";

    public static string Body(AlertNotification n)
    {
        var a = n.Alert;
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{Verb(n.Kind)} — {a.Severity}");
        sb.AppendLine(a.Title);
        sb.AppendLine();
        sb.AppendLine(a.Message);
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Device:     {a.DeviceName ?? "—"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"State:      {a.State}{(a.IsFlapping ? " (flapping)" : string.Empty)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"First seen: {Time(a.FirstSeenUtc)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Last seen:  {Time(a.LastSeenUtc)}");
        if (a.ResolvedUtc is { } r)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Resolved:   {Time(r)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Alert ID:   {a.Id}");
        return sb.ToString();
    }
}

/// <summary>Writes alerts to the local Windows Application event log (source "NetWatch").</summary>
internal static class EventLogChannel
{
    public const string Source = "NetWatch";

    public static void Write(AlertNotification n)
    {
        try
        {
            if (!EventLog.SourceExists(Source))
            {
                EventLog.CreateEventSource(Source, "Application");
            }
        }
        catch (System.Security.SecurityException)
        {
            // Not elevated (console mode): writing still works if the installer/service registered the source.
        }

        var type = n.Kind == NotificationKind.Resolved
            ? EventLogEntryType.Information
            : n.Alert.Severity switch
            {
                Severity.Critical or Severity.Major => EventLogEntryType.Error,
                Severity.Warning => EventLogEntryType.Warning,
                _ => EventLogEntryType.Information,
            };

        // Event IDs: 1000 + severity for raised, 2000 resolved, 3000 + severity escalated, 9000 test.
        var id = n.Kind switch
        {
            NotificationKind.Raised => 1000 + (int)n.Alert.Severity,
            NotificationKind.Resolved => 2000,
            NotificationKind.Escalated => 3000 + (int)n.Alert.Severity,
            _ => 9000,
        };

        EventLog.WriteEntry(Source, NotificationText.Body(n), type, id);
    }
}

/// <summary>Forwards alerts as RFC 5424 syslog messages to an existing SIEM/collector.</summary>
internal static class SyslogForwardChannel
{
    private const int Facility = 16; // local0

    public static string Format(AlertNotification n, string hostname)
    {
        var a = n.Alert;
        var severity = n.Kind == NotificationKind.Resolved
            ? 5
            : a.Severity switch
            {
                Severity.Critical => 2,
                Severity.Major => 3,
                Severity.Warning => 4,
                _ => 6,
            };
        var pri = (Facility * 8) + severity;
        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var sd = string.Create(CultureInfo.InvariantCulture,
            $"[netwatch@32473 event=\"{Esc(NotificationText.Verb(n.Kind))}\" id=\"{a.Id}\" severity=\"{a.Severity}\" state=\"{a.State}\" device=\"{Esc(a.DeviceName ?? "-")}\"]");
        var msg = $"{a.Title}: {a.Message}".Replace('\r', ' ').Replace('\n', ' ');
        return string.Create(CultureInfo.InvariantCulture, $"<{pri}>1 {ts} {hostname} NetWatch - ALERT {sd} {msg}");
    }

    private static string Esc(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("]", "\\]");

    public static async Task SendAsync(IReadOnlyList<AlertNotification> batch, NotificationSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.SyslogForwardHost))
        {
            throw new InvalidOperationException("No syslog target host configured.");
        }

        var host = Environment.MachineName;
        var messages = batch.Select(n => Encoding.UTF8.GetBytes(Format(n, host))).ToList();
        if (s.SyslogForwardProtocol == ForwardProtocol.Udp)
        {
            using var udp = new UdpClient();
            foreach (var m in messages)
            {
                await udp.SendAsync(m, s.SyslogForwardHost, s.SyslogForwardPort, ct);
            }

            return;
        }

        // TCP with octet-counting framing (RFC 6587).
        using var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await tcp.ConnectAsync(s.SyslogForwardHost, s.SyslogForwardPort, timeout.Token);
        await using var stream = tcp.GetStream();
        foreach (var m in messages)
        {
            var frame = Encoding.ASCII.GetBytes(m.Length.ToString(CultureInfo.InvariantCulture) + " ");
            await stream.WriteAsync(frame, timeout.Token);
            await stream.WriteAsync(m, timeout.Token);
        }

        await stream.FlushAsync(timeout.Token);
    }
}

/// <summary>
/// Runs an administrator-provided script from the data folder's "scripts" directory. Alert data is passed in
/// environment variables and as JSON on standard input, never on a command line, so alert text cannot inject commands.
/// </summary>
internal static partial class ScriptChannel
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._ -]{0,100}\.(ps1|cmd|bat|exe)$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeFileName();

    /// <summary>Resolves and validates the script path. Returns the full path or throws with a readable message.</summary>
    public static string Resolve(NetWatchPaths paths, string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !SafeFileName().IsMatch(file.Trim()))
        {
            throw new InvalidOperationException("The script must be a .ps1, .cmd, .bat or .exe file name (no folders).");
        }

        var dir = Path.GetFullPath(paths.ScriptsDir);
        var full = Path.GetFullPath(Path.Combine(dir, file.Trim()));
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The script must be located in the scripts folder.");
        }

        if (!File.Exists(full))
        {
            throw new InvalidOperationException($"Script not found: {full}");
        }

        return full;
    }

    public static async Task RunAsync(AlertNotification n, NotificationSettings s, NetWatchPaths paths, CancellationToken ct)
    {
        var script = Resolve(paths, s.ScriptFile);
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = paths.ScriptsDir,
        };

        switch (Path.GetExtension(script).ToLowerInvariant())
        {
            case ".ps1":
                psi.FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
                {
                    psi.ArgumentList.Add(arg);
                }

                break;
            case ".cmd":
            case ".bat":
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(script);
                break;
            default:
                psi.FileName = script;
                break;
        }

        var a = n.Alert;
        psi.Environment["NETWATCH_EVENT"] = NotificationText.Verb(n.Kind);
        psi.Environment["NETWATCH_ALERT_ID"] = a.Id.ToString(CultureInfo.InvariantCulture);
        psi.Environment["NETWATCH_SEVERITY"] = a.Severity.ToString();
        psi.Environment["NETWATCH_STATE"] = a.State.ToString();
        psi.Environment["NETWATCH_TITLE"] = a.Title;
        psi.Environment["NETWATCH_MESSAGE"] = a.Message;
        psi.Environment["NETWATCH_DEVICE"] = a.DeviceName ?? string.Empty;
        psi.Environment["NETWATCH_DEVICE_ID"] = a.DeviceId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        psi.Environment["NETWATCH_FIRST_SEEN_UTC"] = a.FirstSeenUtc.ToString("O", CultureInfo.InvariantCulture);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Script could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { @event = NotificationText.Verb(n.Kind), alert = a }));
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The script does not read standard input and has already exited (or closed it): use its exit code.
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(s.ScriptTimeoutSec));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Script did not finish within {s.ScriptTimeoutSec} s and was stopped.");
        }

        if (process.ExitCode != 0)
        {
            var err = (await stderr).Trim();
            if (err.Length == 0)
            {
                err = (await stdout).Trim();
            }

            throw new InvalidOperationException($"Script exited with code {process.ExitCode}: {(err.Length > 500 ? err[..500] : err)}");
        }
    }
}

/// <summary>Sends e-mail through an internal SMTP relay (optional).</summary>
internal static class SmtpChannel
{
    /// <summary>Readable reason why the relay could not be reached.</summary>
    public static string ConnectError(string host, int port, SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => $"The SMTP server name '{host}' cannot be resolved (DNS). Check the name or use the relay's IP address.",
        SocketError.ConnectionRefused => $"{host}:{port} refused the connection: no SMTP service is listening on that port.",
        SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable => $"{host}:{port} cannot be reached (timeout/unreachable): check routing and firewalls.",
        _ => $"Cannot connect to the SMTP server {host}:{port}: {ex.Message}",
    };

    public static IReadOnlyList<MailboxAddress> Recipients(string? to) =>
        (to ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Parse)
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

    /// <summary>
    /// Parses one address. MimeKit also accepts a bare local part ("noc"), which every relay rejects at RCPT and
    /// which then fails the whole message for all recipients, so a domain is required.
    /// </summary>
    public static MailboxAddress? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !MailboxAddress.TryParse(value.Trim(), out var m))
        {
            return null;
        }

        var at = m.Address.LastIndexOf('@');
        return at > 0 && at < m.Address.Length - 1 && !m.Address.AsSpan(at + 1).Contains('@') && !m.Address.Contains(' ', StringComparison.Ordinal) ? m : null;
    }

    public static async Task SendAsync(string subject, string body, NotificationSettings s, ISecretProtector protector, CancellationToken ct, string? toOverride = null, IReadOnlyList<(string Name, byte[] Content, string MimeType)>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(s.SmtpHost))
        {
            throw new InvalidOperationException("No SMTP server configured.");
        }

        var to = Recipients(toOverride ?? s.SmtpTo);
        if (to.Count == 0)
        {
            throw new InvalidOperationException("No valid recipient address configured.");
        }

        if (Parse(s.SmtpFrom) is not { } from)
        {
            throw new InvalidOperationException("The sender address is invalid.");
        }

        var message = new MimeMessage();
        message.From.Add(from);
        message.To.AddRange(to);
        message.Subject = subject;
        var builder = new BodyBuilder { TextBody = body };
        foreach (var (name, content, mime) in attachments ?? [])
        {
            builder.Attachments.Add(name, content, ContentType.Parse(mime));
        }

        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };

        // MailKit's own revocation check is online; the shared policy decides (no check, or offline CRL cache only).
        client.CheckCertificateRevocation = false;
        client.ServerCertificateValidationCallback = (_, certificate, _, errors) =>
            certificate is not null && (errors == System.Net.Security.SslPolicyErrors.None || NetWatch.Core.Configuration.TlsPolicy.Verify(certificate, s.SmtpHost ?? string.Empty, out _));
        var options = s.SmtpSecurity switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        };
        try
        {
            await client.ConnectAsync(s.SmtpHost, s.SmtpPort, options, ct);
        }
        catch (SocketException ex)
        {
            // BUG-007: name the server and the likely cause (kept an IOException so delivery is retried).
            throw new IOException(ConnectError(s.SmtpHost, s.SmtpPort, ex), ex);
        }
        if (!string.IsNullOrWhiteSpace(s.SmtpUsername))
        {
            var password = string.IsNullOrEmpty(s.SmtpPasswordProtected)
                ? string.Empty
                : protector.Unprotect(Convert.FromBase64String(s.SmtpPasswordProtected));
            await client.AuthenticateAsync(s.SmtpUsername, password, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Sends e-mail with attachments (scheduled reports) through the configured SMTP relay.</summary>
public sealed class MailSender
{
    private readonly NetWatch.Data.Config.SettingsService _settings;
    private readonly ISecretProtector _protector;

    public MailSender(NetWatch.Data.Config.SettingsService settings, ISecretProtector protector)
    {
        _settings = settings;
        _protector = protector;
    }

    /// <summary>True when an SMTP server and sender are configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Notifications.SmtpHost) && !string.IsNullOrWhiteSpace(_settings.Notifications.SmtpFrom);

    public static bool ValidRecipients(string? to, out int count)
    {
        var parts = (to ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        count = SmtpChannel.Recipients(to).Count;
        return parts.Length == count && count is > 0 and <= 20;
    }

    /// <summary>True for one address with a local part and a domain ("noc@corp.local").</summary>
    public static bool ValidAddress(string? address) => SmtpChannel.Parse(address) is not null;

    public Task SendAsync(string to, string subject, string body, IReadOnlyList<(string Name, byte[] Content, string MimeType)> attachments, CancellationToken ct) =>
        SmtpChannel.SendAsync(subject, body, _settings.Notifications, _protector, ct, to, attachments);
}
