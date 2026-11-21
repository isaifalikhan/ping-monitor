using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetWatch.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NetWatch.Collectors.Servers.Linux;

/// <summary>
/// Agentless Linux collection over SSH: one read-only shell command gathers /proc statistics, disk usage
/// (df), systemd unit states and the reboot-required flag. The host key is pinned on first contact (trust on
/// first use); a changed key is refused until an administrator clears it.
/// </summary>
public sealed partial class SshServerCollector : IServerCollector
{
    private readonly ILogger<SshServerCollector> _logger;

    public SshServerCollector(ILogger<SshServerCollector> logger)
    {
        _logger = logger;
    }

    [GeneratedRegex(@"^[A-Za-z0-9@._:\-]{1,200}$")]
    internal static partial Regex SafeUnitName();

    /// <summary>The command run on the server. Only reads files and queries systemd; unit names are validated.</summary>
    internal static string BuildScript(IReadOnlyList<string> units, bool gpus = false, bool nvidiaSmi = false)
    {
        var sb = new StringBuilder();
        sb.Append("export LC_ALL=C; ");
        sb.Append("echo '##STAT1'; head -n1 /proc/stat; sleep 1; echo '##STAT2'; head -n1 /proc/stat; ");
        sb.Append("echo '##MEM'; cat /proc/meminfo; ");
        sb.Append("echo '##UP'; cat /proc/uptime; ");
        sb.Append("echo '##LOAD'; cat /proc/loadavg; ");
        sb.Append("echo '##NPROC'; (nproc 2>/dev/null || grep -c ^processor /proc/cpuinfo); ");
        sb.Append("echo '##DF'; df -PkT -x tmpfs -x devtmpfs -x squashfs -x overlay -x efivarfs 2>/dev/null; ");
        sb.Append("echo '##NET'; cat /proc/net/dev; ");
        sb.Append("echo '##OS'; (. /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\"); uname -r; ");
        var safe = units.Where(u => SafeUnitName().IsMatch(u)).ToList();
        if (safe.Count > 0)
        {
            sb.Append("echo '##SVC'; systemctl show -p Id -p LoadState -p ActiveState -p Description ");
            sb.Append(string.Join(' ', safe.Select(u => $"'{u}'")));
            sb.Append(" 2>/dev/null; ");
        }

        if (gpus)
        {
            // lspci names the GPUs (also when the driver is broken); nvidia-smi adds the values for NVIDIA cards.
            sb.Append("echo '##LSPCI'; (lspci 2>/dev/null | grep -iE 'vga|3d controller|display controller'); ");
            if (nvidiaSmi)
            {
                sb.Append("echo '##NVSMI'; if command -v nvidia-smi >/dev/null 2>&1; then ").Append(GpuParsers.NvidiaSmiCommand).Append(" 2>&1; fi; ");
            }
        }

        sb.Append("echo '##REBOOT'; if [ -f /var/run/reboot-required ]; then echo yes; else echo no; fi");
        return sb.ToString();
    }

    public async Task<ServerPollData> CollectAsync(ServerPollRequest request, CancellationToken ct)
    {
        var data = new ServerPollData { Method = ServerCollectionMethod.Ssh };
        if (request.Credential?.Secret is not { } secret || string.IsNullOrWhiteSpace(secret.Username))
        {
            data.Error = "No SSH credential assigned.";
            return data;
        }

        string? seenFingerprint = null;
        try
        {
            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrWhiteSpace(secret.PrivateKey))
            {
                using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(secret.PrivateKey));
                var key = string.IsNullOrEmpty(secret.Password) ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, secret.Password);
                methods.Add(new PrivateKeyAuthenticationMethod(secret.Username, key));
            }

            if (!string.IsNullOrEmpty(secret.Password))
            {
                methods.Add(new PasswordAuthenticationMethod(secret.Username, secret.Password));
            }

            var info = new ConnectionInfo(request.Host, request.SshPort, secret.Username, methods.ToArray()) { Timeout = request.Timeout };
            using var client = new SshClient(info);
            client.HostKeyReceived += (_, e) =>
            {
                seenFingerprint = e.FingerPrintSHA256;
                e.CanTrust = request.SshHostKeyFingerprint is null ||
                             string.Equals(request.SshHostKeyFingerprint, e.FingerPrintSHA256, StringComparison.Ordinal);
            };

            await client.ConnectAsync(ct).WaitAsync(request.Timeout + TimeSpan.FromSeconds(5), ct);
            using var command = client.CreateCommand(BuildScript(request.WatchedServices, request.ReadGpus, request.RunNvidiaSmi));
            command.CommandTimeout = request.Timeout;
            await command.ExecuteAsync(ct).WaitAsync(request.Timeout + TimeSpan.FromSeconds(5), ct);
            var output = command.Result;
            client.Disconnect();

            LinuxStatsParser.Parse(output, request.WatchedServices, DateTime.UtcNow, data);
            data.Ok = data.OsName is not null || data.CpuPct is not null || data.MemPct is not null;
            if (!data.Ok)
            {
                data.Error = "The SSH command produced no usable output (is /proc available?).";
            }

            if (request.SshHostKeyFingerprint is null && seenFingerprint is not null)
            {
                data.NewSshHostKeyFingerprint = seenFingerprint;
            }
        }
        catch (SshConnectionException ex) when (request.SshHostKeyFingerprint is not null && seenFingerprint is not null &&
                                                seenFingerprint != request.SshHostKeyFingerprint)
        {
            data.Error = $"SSH host key changed (expected SHA256:{request.SshHostKeyFingerprint}, got SHA256:{seenFingerprint}). " +
                         "If the server was reinstalled, clear the stored host key on the device page.";
            _logger.LogWarning(ex, "SSH host key mismatch for {Host}", request.Host);
        }
        catch (SshAuthenticationException)
        {
            data.Error = "SSH authentication failed (check user name, password or key).";
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            data.Error = ex is OperationCanceledException or TimeoutException ? "SSH connection timed out." : $"SSH: {ex.Message}";
        }

        return data;
    }
}

/// <summary>Parses the output of <see cref="SshServerCollector.BuildScript"/>. Pure function (unit tested).</summary>
public static class LinuxStatsParser
{
    public static void Parse(string output, IReadOnlyList<string> watchedUnits, DateTime nowUtc, ServerPollData data)
    {
        var sections = Split(output);

        if (sections.TryGetValue("STAT1", out var s1) && sections.TryGetValue("STAT2", out var s2) &&
            CpuTimes(s1.FirstOrDefault()) is { } a && CpuTimes(s2.FirstOrDefault()) is { } b)
        {
            var total = b.Total - a.Total;
            var idle = b.Idle - a.Idle;
            if (total > 0)
            {
                data.CpuPct = Math.Round(Math.Clamp(100.0 * (total - idle) / total, 0, 100), 1);
            }
        }

        if (sections.TryGetValue("MEM", out var mem))
        {
            var kv = mem.Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => ParseKb(p[1]), StringComparer.Ordinal);
            if (kv.GetValueOrDefault("MemTotal") is { } total and > 0)
            {
                var available = kv.GetValueOrDefault("MemAvailable")
                                ?? (kv.GetValueOrDefault("MemFree") ?? 0) + (kv.GetValueOrDefault("Buffers") ?? 0) + (kv.GetValueOrDefault("Cached") ?? 0);
                data.MemTotalBytes = total * 1024;
                data.MemPct = Math.Round(100.0 * (total - available) / total, 1);
            }
        }

        if (sections.TryGetValue("UP", out var up) && up.FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } upText &&
            double.TryParse(upText, NumberStyles.Float, CultureInfo.InvariantCulture, out var upSec))
        {
            data.LastBootUtc = nowUtc.AddSeconds(-upSec);
        }

        if (sections.TryGetValue("LOAD", out var load) && load.FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 3 } l)
        {
            data.Load1 = D(l[0]);
            data.Load5 = D(l[1]);
            data.Load15 = D(l[2]);
        }

        if (sections.TryGetValue("NPROC", out var nproc) && int.TryParse(nproc.FirstOrDefault()?.Trim(), out var n) && n > 0)
        {
            data.CpuCount = n;
        }

        if (sections.TryGetValue("DF", out var df))
        {
            foreach (var line in df.Skip(1))
            {
                // Filesystem Type 1024-blocks Used Available Capacity Mounted on
                var f = line.Split(' ', 7, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 7 || !long.TryParse(f[2], out var blocks) || !long.TryParse(f[4], out var avail) || blocks <= 0)
                {
                    continue;
                }

                data.Volumes.Add(new VolumeReading(f[6].Trim(), f[0], f[1], blocks * 1024, avail * 1024));
            }
        }

        if (sections.TryGetValue("NET", out var net))
        {
            ulong rx = 0, tx = 0;
            var any = false;
            foreach (var line in net)
            {
                var colon = line.IndexOf(':');
                if (colon < 0 || line[..colon].Trim() == "lo")
                {
                    continue;
                }

                var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 9 && ulong.TryParse(f[0], out var r) && ulong.TryParse(f[8], out var t))
                {
                    rx += r;
                    tx += t;
                    any = true;
                }
            }

            if (any)
            {
                data.NetRxBytes = rx;
                data.NetTxBytes = tx;
            }
        }

        if (sections.TryGetValue("OS", out var os))
        {
            var lines = os.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (lines.Count >= 2)
            {
                data.OsName = lines[0].Trim();
                data.OsVersion = lines[1].Trim();
            }
            else if (lines.Count == 1)
            {
                data.OsName = "Linux";
                data.OsVersion = lines[0].Trim();
            }
        }

        if (watchedUnits.Count > 0)
        {
            var blocks = sections.TryGetValue("SVC", out var svc) ? UnitBlocks(svc) : new List<Dictionary<string, string>>();
            var safe = watchedUnits.Where(u => SshServerCollector.SafeUnitName().IsMatch(u)).ToList();
            for (var i = 0; i < safe.Count; i++)
            {
                var block = i < blocks.Count ? blocks[i] : null;
                var state = block is null ? ServiceState.Unknown
                    : block.GetValueOrDefault("LoadState") == "not-found" ? ServiceState.NotFound
                    : MapActiveState(block.GetValueOrDefault("ActiveState"));
                data.Services.Add(new ServiceReading(safe[i], block?.GetValueOrDefault("Description"), state, null));
            }
        }

        if (sections.TryGetValue("REBOOT", out var reboot) && reboot.FirstOrDefault()?.Trim() is { } r2)
        {
            data.PendingReboot = r2 == "yes";
        }

        if (sections.TryGetValue("LSPCI", out var pci))
        {
            data.GpusRead = true;
            var listed = GpuParsers.FromLspci(pci);
            var (smi, error) = GpuParsers.ParseNvidiaSmi(sections.TryGetValue("NVSMI", out var nv) ? string.Join("\n", nv) : null);
            var merged = GpuParsers.MergeNvidia(listed, smi, error ?? (listed.Any(g => g.Vendor == "NVIDIA") && smi.Count == 0 && sections.ContainsKey("NVSMI") ? "nvidia-smi not installed or reported no GPU" : null));
            data.Gpus.AddRange(merged);
            data.GpuError = error is null ? null : $"nvidia-smi: {error}";
        }
    }

    public static ServiceState MapActiveState(string? state) => state switch
    {
        "active" or "reloading" => ServiceState.Running,
        "inactive" => ServiceState.Stopped,
        "failed" => ServiceState.Failed,
        "activating" => ServiceState.StartPending,
        "deactivating" => ServiceState.StopPending,
        _ => ServiceState.Unknown,
    };

    private static Dictionary<string, List<string>> Split(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in output.Replace("\r", string.Empty).Split('\n'))
        {
            if (raw.StartsWith("##", StringComparison.Ordinal))
            {
                current = new List<string>();
                sections[raw[2..].Trim()] = current;
            }
            else
            {
                current?.Add(raw);
            }
        }

        return sections;
    }

    private static List<Dictionary<string, string>> UnitBlocks(List<string> lines)
    {
        var blocks = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                current = null;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            if (current is null)
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                blocks.Add(current);
            }

            current[line[..eq]] = line[(eq + 1)..];
        }

        return blocks;
    }

    private static (double Total, double Idle)? CpuTimes(string? line)
    {
        if (line is null || !line.StartsWith("cpu", StringComparison.Ordinal))
        {
            return null;
        }

        var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0).ToArray();
        if (v.Length < 4)
        {
            return null;
        }

        // user nice system idle iowait irq softirq steal (guest values are already included in user/nice).
        var total = v.Take(Math.Min(8, v.Length)).Sum();
        var idle = v[3] + (v.Length > 4 ? v[4] : 0);
        return (total, idle);
    }

    private static long? ParseKb(string value)
    {
        var part = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
