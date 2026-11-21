using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Data.Config;

namespace NetWatch.Alerts.Notifications;

/// <summary>Delivery status of one notification channel (shown on the settings page).</summary>
public sealed record ChannelStatus(string Channel, long Sent, long Failed, DateTime? LastSuccessUtc, DateTime? LastErrorUtc, string? LastError);

/// <summary>
/// Delivers alert notifications over the configured offline channels: Windows Event Log, syslog forwarding,
/// a local script and SMTP. Runs on its own queue so a slow mail server never delays the alert engine.
/// Bursts are coalesced: when many alerts arrive at once they are e-mailed as one digest.
/// </summary>
public sealed class NotificationDispatcher : BackgroundService, INotificationSink
{
    public const string EventLog = "eventlog";
    public const string Syslog = "syslog";
    public const string Script = "script";
    public const string Smtp = "smtp";

    private const int DigestThreshold = 5;

    private readonly SettingsService _settings;
    private readonly ISecretProtector _protector;
    private readonly NetWatchPaths _paths;
    private readonly ILogger<NotificationDispatcher> _logger;
    private readonly Channel<AlertNotification> _queue = Channel.CreateBounded<AlertNotification>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<string, ChannelStatus> _status = new(StringComparer.Ordinal);

    public NotificationDispatcher(SettingsService settings, ISecretProtector protector, NetWatchPaths paths, ILogger<NotificationDispatcher> logger)
    {
        _settings = settings;
        _protector = protector;
        _paths = paths;
        _logger = logger;
    }

    public IReadOnlyList<ChannelStatus> Status =>
        new[] { EventLog, Syslog, Script, Smtp }.Select(c => _status.GetValueOrDefault(c) ?? new ChannelStatus(c, 0, 0, null, null, null)).ToList();

    public void Enqueue(AlertNotification notification) => _queue.Writer.TryWrite(notification);

    /// <summary>Validates a notification script name; returns its full path or throws <see cref="InvalidOperationException"/>.</summary>
    public static string ValidateScript(NetWatchPaths paths, string? file) => ScriptChannel.Resolve(paths, file);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AlertNotification>();
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                // Give a burst (e.g. a core switch going down) a moment to arrive completely.
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                batch.Clear();
                while (batch.Count < 500 && _queue.Reader.TryRead(out var n))
                {
                    batch.Add(n);
                }

                await DeliverAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static bool Wanted(AlertNotification n, NotificationSettings s, Severity min) => n.Kind switch
    {
        NotificationKind.Resolved => s.NotifyOnResolve && n.Alert.Severity >= min,
        NotificationKind.Escalated => s.NotifyOnEscalation && n.Alert.Severity >= min,
        _ => n.Alert.Severity >= min,
    };

    private async Task DeliverAsync(IReadOnlyList<AlertNotification> batch, CancellationToken ct)
    {
        var s = _settings.Notifications;

        if (s.EventLogEnabled)
        {
            foreach (var n in batch.Where(n => Wanted(n, s, s.EventLogMinSeverity)))
            {
                await TryAsync(EventLog, () =>
                {
                    EventLogChannel.Write(n);
                    return Task.CompletedTask;
                });
            }
        }

        if (s.SyslogForwardEnabled)
        {
            var items = batch.Where(n => Wanted(n, s, s.SyslogForwardMinSeverity)).ToList();
            if (items.Count > 0)
            {
                await TryAsync(Syslog, () => SyslogForwardChannel.SendAsync(items, s, ct), ct, retry: true);
            }
        }

        if (s.ScriptEnabled)
        {
            foreach (var n in batch.Where(n => Wanted(n, s, s.ScriptMinSeverity)))
            {
                await TryAsync(Script, () => ScriptChannel.RunAsync(n, s, _paths, ct));
            }
        }

        if (s.SmtpEnabled)
        {
            var mails = batch.Where(n => Wanted(n, s, s.SmtpMinSeverity)).ToList();
            if (mails.Count >= DigestThreshold)
            {
                var (subject, body) = Digest(mails);
                await TryAsync(Smtp, () => SmtpChannel.SendAsync(subject, body, s, _protector, ct), ct, retry: true);
            }
            else
            {
                foreach (var n in mails)
                {
                    await TryAsync(Smtp, () => SmtpChannel.SendAsync(NotificationText.Subject(n), NotificationText.Body(n), s, _protector, ct), ct, retry: true);
                }
            }
        }
    }

    internal static (string Subject, string Body) Digest(IReadOnlyList<AlertNotification> items)
    {
        var raised = items.Count(i => i.Kind != NotificationKind.Resolved);
        var resolved = items.Count - raised;
        var worst = items.Max(i => i.Alert.Severity);
        var subject = $"[NetWatch] {items.Count} alert notifications ({raised} raised/escalated, {resolved} resolved, worst {worst})";
        var sb = new StringBuilder();
        foreach (var n in items.OrderByDescending(i => i.Alert.Severity).ThenBy(i => i.Alert.FirstSeenUtc))
        {
            sb.AppendLine($"{NotificationText.Verb(n.Kind),-10} {n.Alert.Severity,-8} {NotificationText.Time(n.Alert.LastSeenUtc)}  {n.Alert.Title}");
            sb.AppendLine($"           {n.Alert.Message}");
        }

        return (subject, sb.ToString());
    }

    /// <summary>Sends a test notification over one channel, regardless of its enabled flag. Returns null or the error.</summary>
    public async Task<string?> TestAsync(string channel, CancellationToken ct)
    {
        var s = _settings.Notifications;
        var now = DateTime.UtcNow;
        var test = new AlertNotification(NotificationKind.Test, new AlertDto(
            0, null, null, Environment.MachineName, null, Severity.Info, AlertState.Active, "NetWatch test notification",
            "This is a test message from the NetWatch notification settings.", null, false, now, now, 1,
            null, null, null, null, null, null, false, 0, null));
        try
        {
            switch (channel)
            {
                case EventLog:
                    EventLogChannel.Write(test);
                    break;
                case Syslog:
                    await SyslogForwardChannel.SendAsync([test], s, ct);
                    break;
                case Script:
                    await ScriptChannel.RunAsync(test, s, _paths, ct);
                    break;
                case Smtp:
                    await SmtpChannel.SendAsync(NotificationText.Subject(test), NotificationText.Body(test), s, _protector, ct);
                    break;
                default:
                    return "Unknown channel.";
            }

            Record(channel, null);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Record(channel, ex.Message);
            return ex.Message;
        }
    }

    /// <summary>Waits between delivery attempts over the network channels (SMTP, syslog) after a transient failure.</summary>
    internal IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)];

    /// <summary>Connection-level failures worth another attempt (relay restarting, network blip, 4xx answer).</summary>
    internal static bool IsTransient(Exception ex) => ex switch
    {
        SocketException or IOException or TimeoutException or MailKit.ProtocolException or MailKit.ServiceNotConnectedException => true,
        MailKit.Net.Smtp.SmtpCommandException smtp => (int)smtp.StatusCode is >= 400 and < 500,
        OperationCanceledException => true, // our own connect timeout
        _ => false,
    };

    private async Task TryAsync(string channel, Func<Task> send, CancellationToken ct = default, bool retry = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await send();
                Record(channel, null);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (retry && attempt < RetryDelays.Count && IsTransient(ex))
                {
                    _logger.LogInformation("Notification channel {Channel} failed ({Error}); retrying in {Delay} s", channel, ex.Message, RetryDelays[attempt].TotalSeconds);
                    try
                    {
                        await Task.Delay(RetryDelays[attempt], ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                Record(channel, ex.Message);
                _logger.LogWarning(ex, "Notification channel {Channel} failed", channel);
                return;
            }
        }
    }

    private void Record(string channel, string? error)
    {
        var now = DateTime.UtcNow;
        _status.AddOrUpdate(
            channel,
            _ => error is null ? new ChannelStatus(channel, 1, 0, now, null, null) : new ChannelStatus(channel, 0, 1, null, now, error),
            (_, s) => error is null
                ? s with { Sent = s.Sent + 1, LastSuccessUtc = now }
                : s with { Failed = s.Failed + 1, LastErrorUtc = now, LastError = error });
    }
}
