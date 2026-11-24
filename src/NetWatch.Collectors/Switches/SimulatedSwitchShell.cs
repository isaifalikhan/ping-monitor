using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace NetWatch.Collectors.Switches;

/// <summary>
/// A pretend TP-Link or Junos CLI for the simulator and the tests. It answers like the real switches do: prompts change
/// with the mode, unknown commands and missing entries are reported, and it remembers the static MACs that were set.
/// </summary>
public sealed partial class SimulatedSwitchShell : ICliShell
{
    private readonly SwitchCliKind _kind;
    private readonly string _host;
    private readonly string? _enablePassword;
    private readonly HashSet<string> _statics;
    private readonly IReadOnlyDictionary<int, string> _junosVlans;
    private string _mode = "user";
    private bool _awaitingPassword;

    /// <summary>Every line received, in order (the tests check that passwords are only sent when asked).</summary>
    public List<string> Received { get; } = new();

    public SimulatedSwitchShell(SwitchCliKind kind, string host, HashSet<string>? statics = null, string? enablePassword = null,
                                IReadOnlyDictionary<int, string>? junosVlans = null, bool startAsRootShell = false, bool startPrivileged = false)
    {
        _kind = kind;
        _host = host;
        _enablePassword = enablePassword;
        _statics = statics ?? new HashSet<string>(StringComparer.Ordinal);
        _junosVlans = junosVlans ?? new Dictionary<int, string> { [1] = "default", [10] = "USERS", [20] = "VOICE" };
        _mode = kind == SwitchCliKind.Junos ? (startAsRootShell ? "shell" : "user") : (startPrivileged ? "priv" : "user");
    }

    /// <summary>Static MAC entries as "mac|vlan|interface" (TP-Link) or "mac|vlanName|interface" (Junos).</summary>
    public IReadOnlyCollection<string> Statics => _statics;

    /// <summary>Set to make the next configuration command fail like a switch that refuses it.</summary>
    public string? RefuseWith { get; set; }

    [GeneratedRegex(@"^mac address-table static ([0-9a-f:]{17}) vid (\d+) interface (\S+ \S+)$")]
    private static partial Regex TpAdd();

    [GeneratedRegex(@"^no mac address-table static ([0-9a-f:]{17}) vid (\d+)$")]
    private static partial Regex TpRemove();

    [GeneratedRegex(@"^(set|delete) vlans (\S+) switch-options interface (\S+) static-mac ([0-9a-f:]{17})$")]
    private static partial Regex JunosStatic();

    private string Prompt => _kind == SwitchCliKind.TpLink
        ? _mode switch { "config" => $"{_host}(config)#", "priv" => $"{_host}#", _ => $"{_host}>" }
        : _mode switch { "shell" => "root@:RE:0% ", "config" => $"[edit]\nadmin@{_host}# ", _ => $"admin@{_host}> " };

    public Task<string> ReadBannerAsync(CancellationToken ct) => Task.FromResult($"\r\nWelcome to {_host}\r\n{Prompt}");

    public Task<string> SendAsync(string line, CancellationToken ct)
    {
        Received.Add(line);
        var echo = (_awaitingPassword ? string.Empty : line) + "\r\n";
        var answer = _kind == SwitchCliKind.TpLink ? TpLink(line) : Junos(line);
        return Task.FromResult(echo + answer + (answer.Length > 0 && !answer.EndsWith('\n') ? "\r\n" : string.Empty) + (_awaitingPassword ? "Password:" : Prompt));
    }

    private string TpLink(string line)
    {
        if (_awaitingPassword)
        {
            _awaitingPassword = false;
            if (line == _enablePassword)
            {
                _mode = "priv";
                return string.Empty;
            }

            return "Error: Bad password.";
        }

        switch (_mode, line)
        {
            case ("user", "enable"):
                if (_enablePassword is null)
                {
                    _mode = "priv";
                }
                else
                {
                    _awaitingPassword = true;
                }

                return string.Empty;
            case ("priv", "configure"):
                _mode = "config";
                return string.Empty;
            case ("config", "end"):
                _mode = "priv";
                return string.Empty;
            case ("priv", "copy running-config startup-config"):
                return "Saving user config OK!";
        }

        if (_mode == "config" && RefuseWith is { } refuse)
        {
            RefuseWith = null;
            return refuse;
        }

        if (_mode == "config" && TpAdd().Match(line) is { Success: true } add)
        {
            _statics.RemoveWhere(s => s.StartsWith($"{add.Groups[1].Value}|{add.Groups[2].Value}|", StringComparison.Ordinal));
            _statics.Add($"{add.Groups[1].Value}|{add.Groups[2].Value}|{add.Groups[3].Value}");
            return string.Empty;
        }

        if (_mode == "config" && TpRemove().Match(line) is { Success: true } remove)
        {
            return _statics.RemoveWhere(s => s.StartsWith($"{remove.Groups[1].Value}|{remove.Groups[2].Value}|", StringComparison.Ordinal)) > 0
                ? string.Empty
                : "Error: The entry does not exist.";
        }

        return "Error: Bad command";
    }

    private string Junos(string line)
    {
        switch (_mode, line)
        {
            case ("shell", "cli"):
                _mode = "user";
                return string.Empty;
            case ("user", "show configuration vlans | display set | no-more"):
                return string.Join("\r\n", _junosVlans.Select(v => $"set vlans {v.Value} vlan-id {v.Key}"));
            case ("user", "configure private"):
                _mode = "config";
                return "warning: uncommitted changes will be discarded on exit\r\nEntering configuration mode";
            case ("config", "commit and-quit"):
                _mode = "user";
                return "configuration check succeeds\r\ncommit complete\r\nExiting configuration mode";
            case ("config", "exit configuration-mode"):
                _mode = "user";
                return "Exiting configuration mode";
        }

        if (_mode == "config" && RefuseWith is { } refuse)
        {
            RefuseWith = null;
            return refuse;
        }

        if (_mode == "config" && JunosStatic().Match(line) is { Success: true } m)
        {
            var key = $"{m.Groups[4].Value}|{m.Groups[2].Value}|{m.Groups[3].Value}";
            if (!_junosVlans.Values.Contains(m.Groups[2].Value))
            {
                return "error: vlan not found";
            }

            if (m.Groups[1].Value == "set")
            {
                _statics.Add(key);
                return string.Empty;
            }

            return _statics.Remove(key) ? string.Empty : "warning: statement not found";
        }

        return "\r\n                      ^\r\nsyntax error.";
    }
}

/// <summary>Static MACs set on simulated switches, so the simulated MAC table shows them at the next read.</summary>
public sealed class SimulatedSwitchConfig
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<(string Mac, int Vlan), int>> _added = new();
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<(string Mac, int Vlan), byte>> _removed = new();

    public void Apply(int deviceId, int ifIndex, string mac, int vlan, bool add)
    {
        var added = _added.GetOrAdd(deviceId, _ => new());
        var removed = _removed.GetOrAdd(deviceId, _ => new());
        if (add)
        {
            added[(mac, vlan)] = ifIndex;
            removed.TryRemove((mac, vlan), out _);
        }
        else
        {
            added.TryRemove((mac, vlan), out _);
            removed[(mac, vlan)] = 0;
        }
    }

    public IReadOnlyDictionary<(string Mac, int Vlan), int> Added(int deviceId) =>
        _added.TryGetValue(deviceId, out var a) ? a : new Dictionary<(string Mac, int Vlan), int>();

    public bool IsRemoved(int deviceId, string mac, int? vlan) =>
        _removed.TryGetValue(deviceId, out var r) && r.ContainsKey((mac, vlan ?? 1));
}
