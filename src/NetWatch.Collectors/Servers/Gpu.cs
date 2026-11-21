using System.Globalization;
using System.Text.RegularExpressions;
using NetWatch.Collectors.Servers.Windows;

namespace NetWatch.Collectors.Servers;

/// <summary>One GPU as read from a server. Values that could not be read stay null.</summary>
public sealed record GpuReading(
    int Index,
    string Name,
    string? Vendor,
    string? DriverVersion = null,
    double? UtilizationPct = null,
    double? MemoryUsedMb = null,
    double? MemoryTotalMb = null,
    double? TemperatureC = null,
    double? PowerW = null,
    double? FanPct = null,
    string? Problem = null)
{
    public double? MemoryPct => MemoryUsedMb is { } used && MemoryTotalMb is { } total && total > 0 ? Math.Round(used / total * 100, 1) : null;
}

/// <summary>
/// Pure parsers for GPU data: Win32_VideoController (inventory), the Windows "GPU Engine" / "GPU Adapter Memory"
/// performance counters (utilization, memory), nvidia-smi CSV (NVIDIA temperature, power, fan) and lspci.
/// </summary>
public static partial class GpuParsers
{
    /// <summary>The nvidia-smi query NetWatch runs (index first, driver last; values without units).</summary>
    public const string NvidiaSmiFields = "index,name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,fan.speed,driver_version";

    public const string NvidiaSmiCommand = "nvidia-smi --query-gpu=" + NvidiaSmiFields + " --format=csv,noheader,nounits";

    /// <summary>
    /// Physical GPUs from Win32_VideoController: NVIDIA (PCI vendor 10DE), AMD (1002) and Intel data-centre/Arc
    /// GPUs. Server VGA chips of the management controller (Matrox G200, ASPEED), hypervisor and remote display
    /// adapters are not GPUs and are ignored.
    /// </summary>
    public static List<GpuReading> FromVideoControllers(IEnumerable<WmiRow> rows)
    {
        var result = new List<GpuReading>();
        foreach (var r in rows)
        {
            var name = r.GetValueOrDefault("Name")?.Trim();
            var pnp = r.GetValueOrDefault("PNPDeviceID") ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            string? vendor = pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                : pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ? "AMD"
                : pnp.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) && IntelDataCenter().IsMatch(name) ? "Intel"
                : null;
            if (vendor is null)
            {
                continue;
            }

            // AdapterRAM is a uint32: drivers of larger GPUs report 0xFFF00000 (4,293,918,720); that means unknown, not 4 GB.
            double? totalMb = WmiValue.Long(r.GetValueOrDefault("AdapterRAM")) is { } ram && ram is > 0 and < 4_293_918_720 ? Math.Round(ram / 1048576.0) : null;
            var error = WmiValue.Long(r.GetValueOrDefault("ConfigManagerErrorCode")) ?? 0;
            var status = r.GetValueOrDefault("Status");
            string? problem = error != 0
                ? $"device problem code {error}{(error == 43 ? " (driver reported a failure)" : error == 28 ? " (no driver installed)" : error == 22 ? " (disabled)" : string.Empty)}"
                : status is not null && !status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? $"status {status}" : null;
            result.Add(new GpuReading(result.Count, name, vendor, r.GetValueOrDefault("DriverVersion"), MemoryTotalMb: totalMb, Problem: problem));
        }

        return result;
    }

    /// <summary>
    /// Adds utilization (busiest engine type of each physical adapter, as Task Manager shows it) and dedicated
    /// memory in use. Physical adapter N is matched to the N-th GPU.
    /// </summary>
    public static List<GpuReading> ApplyPerfCounters(List<GpuReading> gpus, IEnumerable<WmiRow> engines, IEnumerable<WmiRow> memory)
    {
        var util = engines
            .Select(r => (Name: r.GetValueOrDefault("Name") ?? string.Empty, Value: WmiValue.Double(r.GetValueOrDefault("UtilizationPercentage")) ?? 0))
            .Select(x => (Match: EngineName().Match(x.Name), x.Value))
            .Where(x => x.Match.Success)
            .GroupBy(x => int.Parse(x.Match.Groups["phys"].Value, CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Match.Groups["type"].Value).Max(t => Math.Min(100, t.Sum(x => x.Value))));
        var used = memory
            .Select(r => (Match: AdapterName().Match(r.GetValueOrDefault("Name") ?? string.Empty), Bytes: WmiValue.Double(r.GetValueOrDefault("DedicatedUsage"))))
            .Where(x => x.Match.Success && x.Bytes is not null)
            .GroupBy(x => int.Parse(x.Match.Groups["phys"].Value, CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Bytes!.Value) / 1048576.0);

        return gpus.Select(g => g with
        {
            UtilizationPct = util.TryGetValue(g.Index, out var u) ? Math.Round(u, 1) : g.UtilizationPct,
            MemoryUsedMb = used.TryGetValue(g.Index, out var m) ? Math.Round(m) : g.MemoryUsedMb,
        }).ToList();
    }

    /// <summary>
    /// Parses nvidia-smi CSV output (<see cref="NvidiaSmiFields"/>). Returns the GPUs and, when nvidia-smi failed
    /// (no driver, driver/library mismatch, no devices), the error text.
    /// </summary>
    public static (List<GpuReading> Gpus, string? Error) ParseNvidiaSmi(string? output)
    {
        var gpus = new List<GpuReading>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return (gpus, null);
        }

        string? error = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cells = line.Split(',').Select(c => c.Trim()).ToArray();
            if (cells.Length < 9 || !int.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (line.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || line.Contains("No devices", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    error ??= line.Length > 300 ? line[..300] : line;
                }

                continue;
            }

            gpus.Add(new GpuReading(index, cells[1], "NVIDIA", cells[8].StartsWith('[') || cells[8].Length == 0 ? null : cells[8],
                N(cells[2]), N(cells[3]), N(cells[4]), N(cells[5]), N(cells[6]), N(cells[7])));
        }

        return (gpus, error);

        // "[N/A]", "[Not Supported]", "N/A" → null.
        static double? N(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// Merges nvidia-smi readings into the GPU list (by order among NVIDIA GPUs). On Linux there is no controller
    /// list: lspci names the GPUs, nvidia-smi fills the values, and a GPU in lspci that nvidia-smi does not report
    /// is a driver problem.
    /// </summary>
    public static List<GpuReading> MergeNvidia(List<GpuReading> gpus, List<GpuReading> smi, string? smiError)
    {
        if (gpus.Count == 0)
        {
            return smi.ToList();
        }

        var result = new List<GpuReading>();
        var nvidiaOrder = 0;
        foreach (var g in gpus)
        {
            if (g.Vendor != "NVIDIA")
            {
                result.Add(g);
                continue;
            }

            var s = nvidiaOrder < smi.Count ? smi[nvidiaOrder] : null;
            nvidiaOrder++;
            result.Add(s is null
                ? g with { Problem = g.Problem ?? (smiError is not null ? $"nvidia-smi: {smiError}" : null) }
                : g with
                {
                    DriverVersion = s.DriverVersion ?? g.DriverVersion,
                    UtilizationPct = s.UtilizationPct ?? g.UtilizationPct,
                    MemoryUsedMb = s.MemoryUsedMb ?? g.MemoryUsedMb,
                    MemoryTotalMb = s.MemoryTotalMb ?? g.MemoryTotalMb,
                    TemperatureC = s.TemperatureC,
                    PowerW = s.PowerW,
                    FanPct = s.FanPct,
                });
        }

        return result;
    }

    /// <summary>GPUs from "lspci" lines (VGA / 3D / Display controllers of NVIDIA, AMD or Intel data-centre GPUs).</summary>
    public static List<GpuReading> FromLspci(IEnumerable<string> lines)
    {
        var result = new List<GpuReading>();
        foreach (var line in lines)
        {
            var m = LspciLine().Match(line);
            if (!m.Success)
            {
                continue;
            }

            var desc = m.Groups["desc"].Value.Trim();
            string? vendor = desc.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                : desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) || desc.Contains("ATI", StringComparison.Ordinal) ? "AMD"
                : desc.StartsWith("Intel", StringComparison.OrdinalIgnoreCase) && IntelDataCenter().IsMatch(desc) ? "Intel"
                : null;
            if (vendor is not null)
            {
                result.Add(new GpuReading(result.Count, desc.Length > 200 ? desc[..200] : desc, vendor));
            }
        }

        return result;
    }

    [GeneratedRegex(@"phys_(?<phys>\d+)_eng_\d+_engtype_(?<type>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex EngineName();

    [GeneratedRegex(@"phys_(?<phys>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AdapterName();

    [GeneratedRegex(@"^\S+\s+(VGA compatible controller|3D controller|Display controller)[^:]*:\s*(?<desc>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LspciLine();

    [GeneratedRegex(@"Arc|Flex|Data Center GPU|Max \d", RegexOptions.IgnoreCase)]
    private static partial Regex IntelDataCenter();
}
