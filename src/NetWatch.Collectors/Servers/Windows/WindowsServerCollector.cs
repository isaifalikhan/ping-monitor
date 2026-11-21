using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetWatch.Core;

namespace NetWatch.Collectors.Servers.Windows;

/// <summary>
/// Agentless Windows server collection: WMI over WinRM (falling back to DCOM) for CPU, memory, disks, network,
/// uptime, watched services, pending reboot and last patch; remote event-log reading for System/Application
/// Critical and Error entries. Each query fails independently so one missing class never hides the rest.
/// </summary>
public sealed partial class WindowsServerCollector : IServerCollector
{
    private const string Cimv2 = "root/cimv2";
    private const uint HkeyLocalMachine = 0x80000002;

    private static readonly string[] EventLogNames = ["System", "Application"];

    private readonly ILogger<WindowsServerCollector> _logger;

    public WindowsServerCollector(ILogger<WindowsServerCollector> logger)
    {
        _logger = logger;
    }

    [GeneratedRegex(@"^[A-Za-z0-9 _.$@\-]{1,256}$")]
    internal static partial Regex SafeServiceName();

    public async Task<ServerPollData> CollectAsync(ServerPollRequest request, CancellationToken ct)
    {
        var account = request.Credential is { } c ? WindowsAccount.From(c.Secret) : (WindowsAccount?)null;
        var data = new ServerPollData();
        var errors = new List<string>();

        // WinRM first (firewall friendly, encrypted), then DCOM.
        var attempts = new List<Func<IWmiSession>>
        {
            () => new WinRmWmiSession(request.Host, request.WinRmPort, request.WinRmHttps, account, request.Timeout),
        };
        if (request.DcomFallback)
        {
            attempts.Add(() => new DcomWmiSession(request.Host, account, request.Timeout));
        }

        foreach (var open in attempts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var collected = await Task.Run(() => CollectWmi(open, request), ct).WaitAsync(request.Timeout + TimeSpan.FromSeconds(10), ct);
                CopyInto(collected, data);
                data.Ok = true;
                data.Error = null;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add(Describe(ex));
                _logger.LogDebug(ex, "WMI collection attempt failed for {Host}", request.Host);
            }
        }

        if (!data.Ok)
        {
            data.Error = string.Join(" | ", errors);
        }

        if (request.ReadEventLogs && data.Ok)
        {
            try
            {
                await Task.Run(() => ReadEventLogs(request, account, data), ct).WaitAsync(request.Timeout + TimeSpan.FromSeconds(10), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                data.EventLogError = Describe(ex);
                _logger.LogDebug(ex, "Event log read failed for {Host}", request.Host);
            }
        }

        return data;
    }

    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException => "timed out",
        UnauthorizedAccessException => "access denied (check the account's WMI/remote permissions)",
        System.Runtime.InteropServices.COMException com => $"{com.Message.Trim()} (0x{com.HResult:X8})",
        _ => ex.Message.Trim(),
    };

    private ServerPollData CollectWmi(Func<IWmiSession> open, ServerPollRequest request)
    {
        using var session = open();
        var data = new ServerPollData { Method = session.Method };

        // The OS query decides whether this transport works at all: let it throw.
        WindowsStatsParser.ApplyOperatingSystem(session.Query(Cimv2, "SELECT * FROM Win32_OperatingSystem"), data);

        Try(() => WindowsStatsParser.ApplyComputerSystem(session.Query(Cimv2, "SELECT * FROM Win32_ComputerSystem"), data));
        Try(() => WindowsStatsParser.ApplyProcessor(session.Query(Cimv2, "SELECT * FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"), data));
        Try(() => WindowsStatsParser.ApplyDisks(session.Query(Cimv2, "SELECT * FROM Win32_LogicalDisk WHERE DriveType=3"), data));
        Try(() => WindowsStatsParser.ApplyNetwork(session.Query(Cimv2, "SELECT * FROM Win32_PerfFormattedData_Tcpip_NetworkInterface"), data));

        var services = request.WatchedServices.Where(s => SafeServiceName().IsMatch(s)).ToList();
        if (services.Count > 0)
        {
            var where = string.Join(" OR ", services.Select(s => $"Name='{s.Replace("'", "\\'")}'"));
            Try(() => WindowsStatsParser.ApplyServices(session.Query(Cimv2, $"SELECT * FROM Win32_Service WHERE {where}"), services, data));
        }

        Try(() => WindowsStatsParser.ApplyHotfixes(session.Query(Cimv2, "SELECT * FROM Win32_QuickFixEngineering"), data));
        Try(() => data.PendingReboot = PendingReboot(session));
        if (request.ReadGpus)
        {
            Try(() => ReadGpus(session, request, data));
        }

        return data;

        void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "WMI query failed for {Host}", request.Host);
            }
        }
    }

    /// <summary>
    /// GPU inventory (Win32_VideoController), utilization and memory (GPU performance counters, Windows Server
    /// 2019 / WDDM 2.x and later) and, for NVIDIA over WinRM, temperature/power/fan from nvidia-smi.
    /// </summary>
    private void ReadGpus(IWmiSession session, ServerPollRequest request, ServerPollData data)
    {
        var gpus = GpuParsers.FromVideoControllers(session.Query(Cimv2, "SELECT Name, PNPDeviceID, DriverVersion, AdapterRAM, Status, ConfigManagerErrorCode FROM Win32_VideoController"));
        data.GpusRead = true;
        if (gpus.Count == 0)
        {
            return;
        }

        try
        {
            gpus = GpuParsers.ApplyPerfCounters(gpus,
                session.Query(Cimv2, "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"),
                session.Query(Cimv2, "SELECT Name, DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            data.GpuError = "GPU performance counters not available (needs Windows Server 2019 or later and a WDDM 2 driver)";
            _logger.LogDebug(ex, "GPU counters failed for {Host}", request.Host);
        }

        if (request.RunNvidiaSmi && gpus.Any(g => g.Vendor == "NVIDIA") && session is WinRmWmiSession winrm)
        {
            try
            {
                var (smi, error) = GpuParsers.ParseNvidiaSmi(winrm.RunCommand("nvidia-smi", GpuParsers.NvidiaSmiCommand["nvidia-smi ".Length..], TimeSpan.FromSeconds(20)));
                gpus = GpuParsers.MergeNvidia(gpus, smi, error);
                data.GpuError ??= error is null ? null : $"nvidia-smi: {error}";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                data.GpuError ??= $"nvidia-smi over WinRM failed: {Describe(ex)}";
                _logger.LogDebug(ex, "nvidia-smi failed for {Host}", request.Host);
            }
        }

        data.Gpus.AddRange(gpus);
    }

    /// <summary>Checks the registry keys Windows sets when a reboot is required (servicing stack, Windows Update).</summary>
    private static bool? PendingReboot(IWmiSession session)
    {
        bool? Exists(string key)
        {
            var result = session.Invoke("root/default", "StdRegProv", "EnumKey", [("hDefKey", HkeyLocalMachine), ("sSubKeyName", key)]);
            return WmiValue.Long(result.GetValueOrDefault("ReturnValue")) is { } rc ? rc == 0 : null;
        }

        var cbs = Exists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        var wu = Exists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        return cbs is null && wu is null ? null : cbs == true || wu == true;
    }

    private static void CopyInto(ServerPollData src, ServerPollData dst)
    {
        dst.Method = src.Method;
        dst.OsName = src.OsName;
        dst.OsVersion = src.OsVersion;
        dst.LastBootUtc = src.LastBootUtc;
        dst.PendingReboot = src.PendingReboot;
        dst.LastPatchUtc = src.LastPatchUtc;
        dst.LastPatchId = src.LastPatchId;
        dst.CpuPct = src.CpuPct;
        dst.MemPct = src.MemPct;
        dst.MemTotalBytes = src.MemTotalBytes;
        dst.CpuCount = src.CpuCount;
        dst.NetInBps = src.NetInBps;
        dst.NetOutBps = src.NetOutBps;
        dst.Volumes.AddRange(src.Volumes);
        dst.Services.AddRange(src.Services);
    }

    /// <summary>Reads new Critical/Error (optionally Warning) entries from System and Application via a remote event-log session.</summary>
    private void ReadEventLogs(ServerPollRequest request, WindowsAccount? account, ServerPollData data)
    {
        using var session = LocalHost.Is(request.Host) || account is null
            ? (LocalHost.Is(request.Host) ? new EventLogSession() : new EventLogSession(request.Host))
            : new EventLogSession(request.Host, account.Value.Domain, account.Value.User, account.Value.SecurePassword(), SessionAuthentication.Negotiate);

        var levels = request.IncludeWarnings ? "Level=1 or Level=2 or Level=3" : "Level=1 or Level=2";
        var bookmarks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in EventLogNames)
        {
            if (!request.EventBookmarks.TryGetValue(log, out var last))
            {
                // First contact: start after the newest existing record (no flood of historical errors).
                bookmarks[log] = NewestRecordId(session, log) ?? 0;
                continue;
            }

            var xpath = string.Create(CultureInfo.InvariantCulture, $"*[System[({levels}) and EventRecordID > {last}]]");
            var query = new EventLogQuery(log, PathType.LogName, xpath) { Session = session };
            var newest = last;
            using (var reader = new EventLogReader(query))
            {
                for (var i = 0; i < request.MaxEventsPerPoll; i++)
                {
                    using var record = reader.ReadEvent();
                    if (record is null)
                    {
                        break;
                    }

                    var id = record.RecordId ?? 0;
                    newest = Math.Max(newest, id);
                    data.Events.Add(new WindowsEventReading(
                        log,
                        id,
                        (record.TimeCreated ?? DateTime.Now).ToUniversalTime(),
                        record.Level ?? 2,
                        record.Id,
                        record.ProviderName,
                        Truncate(FormatMessage(record))));
                }
            }

            bookmarks[log] = newest;
        }

        data.NewBookmarks = bookmarks;
    }

    private static long? NewestRecordId(EventLogSession session, string log)
    {
        var query = new EventLogQuery(log, PathType.LogName, "*") { Session = session, ReverseDirection = true };
        using var reader = new EventLogReader(query);
        using var record = reader.ReadEvent();
        return record?.RecordId;
    }

    private static string FormatMessage(EventRecord record)
    {
        try
        {
            var text = record.FormatDescription();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }
        catch (EventLogException)
        {
            // Provider metadata unavailable remotely: fall back to the raw insertion strings.
        }

        var props = record.Properties?.Select(p => p.Value?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? [];
        return props.Count > 0 ? string.Join(" | ", props) : $"Event {record.Id} from {record.ProviderName}";
    }

    private static string Truncate(string s) => s.Length <= 4000 ? s : s[..4000] + "…";
}

/// <summary>Maps WMI rows to <see cref="ServerPollData"/>. Pure functions (unit tested).</summary>
public static class WindowsStatsParser
{
    public static void ApplyOperatingSystem(IReadOnlyList<WmiRow> rows, ServerPollData data)
    {
        var os = rows.FirstOrDefault() ?? throw new InvalidOperationException("Win32_OperatingSystem returned no instance.");
        data.OsName = os.GetValueOrDefault("Caption")?.Trim();
        data.OsVersion = os.GetValueOrDefault("Version");
        data.LastBootUtc = WmiValue.DateTime(os.GetValueOrDefault("LastBootUpTime"));
        var totalKb = WmiValue.Long(os.GetValueOrDefault("TotalVisibleMemorySize"));
        var freeKb = WmiValue.Long(os.GetValueOrDefault("FreePhysicalMemory"));
        if (totalKb is > 0 && freeKb is not null)
        {
            data.MemTotalBytes = totalKb * 1024;
            data.MemPct = Math.Round(100.0 * (totalKb.Value - freeKb.Value) / totalKb.Value, 1);
        }
    }

    public static void ApplyComputerSystem(IReadOnlyList<WmiRow> rows, ServerPollData data)
    {
        if (rows.FirstOrDefault() is { } cs && WmiValue.Long(cs.GetValueOrDefault("NumberOfLogicalProcessors")) is { } n and > 0)
        {
            data.CpuCount = (int)n;
        }
    }

    public static void ApplyProcessor(IReadOnlyList<WmiRow> rows, ServerPollData data)
    {
        if (rows.FirstOrDefault() is { } p && WmiValue.Double(p.GetValueOrDefault("PercentProcessorTime")) is { } pct)
        {
            data.CpuPct = Math.Clamp(pct, 0, 100);
        }
    }

    public static void ApplyDisks(IReadOnlyList<WmiRow> rows, ServerPollData data)
    {
        foreach (var d in rows)
        {
            var size = WmiValue.Long(d.GetValueOrDefault("Size"));
            var free = WmiValue.Long(d.GetValueOrDefault("FreeSpace"));
            var name = d.GetValueOrDefault("DeviceID") ?? d.GetValueOrDefault("Name");
            if (string.IsNullOrEmpty(name) || size is not > 0 || free is null)
            {
                continue;
            }

            data.Volumes.Add(new VolumeReading(name, NullIfEmpty(d.GetValueOrDefault("VolumeName")), NullIfEmpty(d.GetValueOrDefault("FileSystem")), size.Value, free.Value));
        }
    }

    public static void ApplyNetwork(IReadOnlyList<WmiRow> rows, ServerPollData data)
    {
        double inBytes = 0, outBytes = 0;
        var any = false;
        foreach (var n in rows)
        {
            var name = n.GetValueOrDefault("Name") ?? string.Empty;
            if (name.Contains("isatap", StringComparison.OrdinalIgnoreCase) || name.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (WmiValue.Double(n.GetValueOrDefault("BytesReceivedPersec")) is { } rx)
            {
                inBytes += rx;
                any = true;
            }

            if (WmiValue.Double(n.GetValueOrDefault("BytesSentPersec")) is { } tx)
            {
                outBytes += tx;
                any = true;
            }
        }

        if (any)
        {
            data.NetInBps = inBytes * 8;
            data.NetOutBps = outBytes * 8;
        }
    }

    public static void ApplyServices(IReadOnlyList<WmiRow> rows, IReadOnlyList<string> watched, ServerPollData data)
    {
        var byName = rows
            .Where(r => r.GetValueOrDefault("Name") is not null)
            .GroupBy(r => r["Name"]!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in watched)
        {
            if (byName.TryGetValue(name, out var r))
            {
                data.Services.Add(new ServiceReading(r["Name"]!, r.GetValueOrDefault("DisplayName"), MapState(r.GetValueOrDefault("State")), r.GetValueOrDefault("StartMode")));
            }
            else
            {
                data.Services.Add(new ServiceReading(name, null, ServiceState.NotFound, null));
            }
        }
    }

    public static void ApplyHotfixes(IReadOnlyList<WmiRow> rows, ServerPollData data)
    {
        DateTime? latest = null;
        string? id = null;
        foreach (var r in rows)
        {
            if (ParseInstalledOn(r.GetValueOrDefault("InstalledOn")) is { } d && (latest is null || d > latest))
            {
                latest = d;
                id = r.GetValueOrDefault("HotFixID");
            }
        }

        data.LastPatchUtc = latest;
        data.LastPatchId = id;
    }

    /// <summary>Win32_QuickFixEngineering.InstalledOn is "M/d/yyyy" text, or occasionally a hex FILETIME.</summary>
    internal static DateTime? ParseInstalledOn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (DateTime.TryParseExact(text, ["M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
        {
            return DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
        }

        if (long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ft) && ft > 0)
        {
            try
            {
                return DateTime.FromFileTimeUtc(ft).Date;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    public static ServiceState MapState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "running" => ServiceState.Running,
        "stopped" => ServiceState.Stopped,
        "start pending" => ServiceState.StartPending,
        "stop pending" => ServiceState.StopPending,
        "paused" or "pause pending" or "continue pending" => ServiceState.Paused,
        _ => ServiceState.Unknown,
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
