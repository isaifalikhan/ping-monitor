using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NetWatch.Collectors.Switches;

/// <summary>An interactive switch CLI session (an SSH shell, or the simulator). Sends one line and returns the output up to the next prompt.</summary>
public interface ICliShell
{
    /// <summary>Output shown after login, up to the first prompt.</summary>
    Task<string> ReadBannerAsync(CancellationToken ct);

    /// <summary>Sends <paramref name="line"/> and returns everything the switch printed until it shows a prompt again.</summary>
    Task<string> SendAsync(string line, CancellationToken ct);
}

/// <summary>Which CLI dialect to speak.</summary>
public enum SwitchCliKind
{
    TpLink,
    Junos
}

/// <summary>Add or remove one static MAC address on one port.</summary>
/// <param name="Mac">12 upper-case hex digits (already validated).</param>
public sealed record StaticMacChange(bool Add, string Mac, int Vlan, string PortName, string? PortDescription);

/// <summary>Result of a change; <see cref="Transcript"/> is what the switch printed (passwords never appear in it).</summary>
public sealed record CliResult(bool Ok, string Message, string Transcript);

/// <summary>Pure helpers that build the CLI commands and read the switch's answers (unit tested).</summary>
public static partial class SwitchCliCommands
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]|\x1B[()][A-Z0-9]|\x1B[=>]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"^\s*[\w.\-@()/:\[\] ]{0,80}[>#%]\s*$")]
    private static partial Regex PromptLine();

    [GeneratedRegex(@"(?i)^\s*(fa|fastethernet|gi|gigabitethernet|tw|two-gigabitethernet|te|tengigabitethernet|ten-gigabitethernet)\s*(\d{1,2}/\d{1,2}/\d{1,3})\b")]
    private static partial Regex TpLinkPort();

    [GeneratedRegex(@"^(ge|xe|et|mge|fe)-(\d{1,2}/\d{1,2}/\d{1,3})(\.\d{1,5})?$")]
    private static partial Regex JunosPort();

    [GeneratedRegex(@"^set vlans (\S+) vlan-id (\d{1,4})\s*$", RegexOptions.Multiline)]
    private static partial Regex JunosVlanLine();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$")]
    private static partial Regex SafeVlanName();

    /// <summary>Removes terminal escape sequences and normalises line ends.</summary>
    public static string Clean(string text) => AnsiEscape().Replace(text, string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>True when the last line of <paramref name="text"/> is a CLI prompt (switch&gt;, switch#, switch(config)#, user@host&gt;, %).</summary>
    public static bool EndsWithPrompt(string text)
    {
        var t = Clean(text).TrimEnd('\n', ' ');
        var last = t[(t.LastIndexOf('\n') + 1)..];
        return last.Length > 0 && PromptLine().IsMatch(last);
    }

    /// <summary>The prompt the output ends with ("" if none).</summary>
    public static string LastLine(string text)
    {
        var t = Clean(text).TrimEnd('\n', ' ');
        return t[(t.LastIndexOf('\n') + 1)..].Trim();
    }

    public static bool AsksForPassword(string text) => LastLine(text).EndsWith("assword:", StringComparison.OrdinalIgnoreCase);

    public static bool AsksToPage(string text)
    {
        var last = LastLine(text);
        return last.Contains("--More--", StringComparison.OrdinalIgnoreCase) || last.Contains("Press any key", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"3C10000A0005" → "3c:10:00:0a:00:05" (the notation both TP-Link and Junos accept).</summary>
    public static string ColonMac(string hex12) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => hex12.Substring(i * 2, 2))).ToLowerInvariant();

    /// <summary>
    /// The TP-Link CLI interface for a port, e.g. "gigabitEthernet 1/0/5", from its ifName ("Gi1/0/5") or ifDescr
    /// ("gigabitEthernet 1/0/5 : copper"). Null when neither looks like a TP-Link port.
    /// </summary>
    public static string? TpLinkInterface(string name, string? description)
    {
        foreach (var candidate in new[] { name, description })
        {
            if (candidate is null || TpLinkPort().Match(candidate) is not { Success: true } m)
            {
                continue;
            }

            var kind = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "fa" or "fastethernet" => "fastEthernet",
                "tw" or "two-gigabitethernet" => "two-gigabitEthernet",
                "te" or "tengigabitethernet" or "ten-gigabitethernet" => "ten-gigabitEthernet",
                _ => "gigabitEthernet",
            };
            return $"{kind} {m.Groups[2].Value}";
        }

        return null;
    }

    /// <summary>The Junos logical interface for a port: "ge-0/0/5" or "ge-0/0/5.0" → "ge-0/0/5.0". Null if not a Junos port name.</summary>
    public static string? JunosInterface(string name, string? description)
    {
        foreach (var candidate in new[] { name, description })
        {
            if (candidate is not null && JunosPort().Match(candidate.Trim()) is { Success: true } m)
            {
                return $"{m.Groups[1].Value}-{m.Groups[2].Value}{(m.Groups[3].Success ? m.Groups[3].Value : ".0")}";
            }
        }

        return null;
    }

    public static string TpLinkCommand(StaticMacChange c, string iface) => c.Add
        ? $"mac address-table static {ColonMac(c.Mac)} vid {c.Vlan.ToString(CultureInfo.InvariantCulture)} interface {iface}"
        : $"no mac address-table static {ColonMac(c.Mac)} vid {c.Vlan.ToString(CultureInfo.InvariantCulture)}";

    public static string JunosCommand(StaticMacChange c, string vlanName, string iface) =>
        $"{(c.Add ? "set" : "delete")} vlans {vlanName} switch-options interface {iface} static-mac {ColonMac(c.Mac)}";

    /// <summary>The Junos VLAN name for an 802.1Q id, from "show configuration vlans | display set". Null if not found.</summary>
    public static string? JunosVlanName(string showOutput, int vlanId)
    {
        foreach (Match m in JunosVlanLine().Matches(Clean(showOutput)))
        {
            if (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) == vlanId && SafeVlanName().IsMatch(m.Groups[1].Value))
            {
                return m.Groups[1].Value;
            }
        }

        return null;
    }

    private static readonly string[] ErrorWords =
    [
        "error", "invalid", "bad command", "incomplete", "unrecognized", "unknown command", "failed", "not found", "does not exist",
        "not exist", "denied", "not allowed", "syntax", "ambiguous", "statement not found", "configuration check-out failed",
    ];

    /// <summary>
    /// The first line of the switch's answer that reports a problem, or null. The echoed command (first line) and the
    /// prompt (last line) are not looked at.
    /// </summary>
    public static string? ErrorIn(string output, string sentCommand)
    {
        var lines = Clean(output).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count > 0 && EndsWithPrompt(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        foreach (var line in lines)
        {
            if (line.EndsWith(sentCommand, StringComparison.Ordinal))
            {
                continue; // the echo of what we typed
            }

            if (ErrorWords.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                return line.Length > 300 ? line[..300] : line;
            }
        }

        return null;
    }
}

/// <summary>
/// Runs the commands of one static-MAC change in an open CLI session, exactly as an engineer would type them in PuTTY.
/// TP-Link: enable → configure → mac address-table static … → end → copy running-config startup-config.
/// Junos: find the VLAN name → configure private → set/delete vlans … static-mac … → commit and-quit.
/// </summary>
public static class SwitchCliDialog
{
    public static Task<CliResult> RunAsync(SwitchCliKind kind, ICliShell shell, StaticMacChange change, string iface, string? enablePassword, CancellationToken ct) =>
        kind == SwitchCliKind.TpLink ? TpLinkAsync(shell, change, iface, enablePassword, ct) : JunosAsync(shell, change, iface, ct);

    private sealed class Log
    {
        private readonly StringBuilder _sb = new();

        public void Add(string text) => _sb.Append(SwitchCliCommands.Clean(text));

        public void Secret() => _sb.Append("********\n");

        public override string ToString() => _sb.Length > 8000 ? _sb.ToString(_sb.Length - 8000, 8000) : _sb.ToString();
    }

    private static async Task<string> SendAsync(ICliShell shell, Log log, string line, CancellationToken ct)
    {
        var output = await shell.SendAsync(line, ct);
        log.Add(output);
        return output;
    }

    private static async Task<CliResult> TpLinkAsync(ICliShell shell, StaticMacChange change, string iface, string? enablePassword, CancellationToken ct)
    {
        var log = new Log();
        var banner = await shell.ReadBannerAsync(ct);
        log.Add(banner);
        var prompt = SwitchCliCommands.LastLine(banner);
        if (prompt.EndsWith('>'))
        {
            var answer = await SendAsync(shell, log, "enable", ct);
            if (SwitchCliCommands.AsksForPassword(answer))
            {
                if (string.IsNullOrEmpty(enablePassword))
                {
                    return new CliResult(false, "The switch asks for an enable password, but the SSH credential has none.", log.ToString());
                }

                answer = await shell.SendAsync(enablePassword, ct);
                log.Secret();
                log.Add(answer);
            }

            if (!SwitchCliCommands.LastLine(answer).EndsWith('#'))
            {
                return new CliResult(false, "The switch did not accept 'enable' (wrong enable password, or the user has no admin level).", log.ToString());
            }
        }
        else if (!prompt.EndsWith('#'))
        {
            return new CliResult(false, $"Unexpected prompt after login: '{prompt}'. Is this a TP-Link switch CLI?", log.ToString());
        }

        var conf = await SendAsync(shell, log, "configure", ct);
        if (!SwitchCliCommands.LastLine(conf).Contains("(config", StringComparison.OrdinalIgnoreCase))
        {
            return new CliResult(false, "The switch did not enter configuration mode ('configure').", log.ToString());
        }

        var command = SwitchCliCommands.TpLinkCommand(change, iface);
        var output = await SendAsync(shell, log, command, ct);
        var error = SwitchCliCommands.ErrorIn(output, command);
        await SendAsync(shell, log, "end", ct);
        if (error is not null)
        {
            return new CliResult(false, $"The switch refused the change: {error}", log.ToString());
        }

        var save = await SendAsync(shell, log, "copy running-config startup-config", ct);
        var saveError = SwitchCliCommands.ErrorIn(save, "copy running-config startup-config");
        return saveError is null
            ? new CliResult(true, change.Add ? "Static MAC added and the configuration saved." : "Static MAC removed and the configuration saved.", log.ToString())
            : new CliResult(false, $"The change was made, but saving the configuration failed: {saveError}. It is lost when the switch restarts unless saved.", log.ToString());
    }

    private static async Task<CliResult> JunosAsync(ICliShell shell, StaticMacChange change, string iface, CancellationToken ct)
    {
        var log = new Log();
        var banner = await shell.ReadBannerAsync(ct);
        log.Add(banner);
        if (SwitchCliCommands.LastLine(banner).EndsWith('%'))
        {
            // root lands in the FreeBSD shell; start the Junos CLI.
            banner = await SendAsync(shell, log, "cli", ct);
        }

        if (!SwitchCliCommands.LastLine(banner).EndsWith('>'))
        {
            return new CliResult(false, $"Unexpected prompt after login: '{SwitchCliCommands.LastLine(banner)}'. Is this a Junos CLI?", log.ToString());
        }

        var vlans = await SendAsync(shell, log, "show configuration vlans | display set | no-more", ct);
        var vlanName = SwitchCliCommands.JunosVlanName(vlans, change.Vlan);
        if (vlanName is null)
        {
            return new CliResult(false, $"VLAN {change.Vlan} is not configured on this switch (no 'vlans … vlan-id {change.Vlan}' in its configuration). " +
                                        "Only ELS switches (EX2300/3400/4300, QFX) are supported.", log.ToString());
        }

        var conf = await SendAsync(shell, log, "configure private", ct);
        if (SwitchCliCommands.ErrorIn(conf, "configure private") is { } confError || !SwitchCliCommands.LastLine(conf).EndsWith('#'))
        {
            return new CliResult(false, $"Could not enter configuration mode: {SwitchCliCommands.ErrorIn(conf, "configure private") ?? SwitchCliCommands.LastLine(conf)}", log.ToString());
        }

        var command = SwitchCliCommands.JunosCommand(change, vlanName, iface);
        var output = await SendAsync(shell, log, command, ct);
        var error = SwitchCliCommands.ErrorIn(output, command) ?? (output.Contains("warning: statement not found", StringComparison.OrdinalIgnoreCase) ? "statement not found" : null);
        if (error is null)
        {
            var commit = await SendAsync(shell, log, "commit and-quit", ct);
            if (commit.Contains("commit complete", StringComparison.OrdinalIgnoreCase))
            {
                return new CliResult(true, change.Add ? "Static MAC added and committed." : "Static MAC removed and committed.", log.ToString());
            }

            error = SwitchCliCommands.ErrorIn(commit, "commit and-quit") ?? "commit did not complete";
        }

        // Leave configuration mode without keeping anything (private mode discards uncommitted changes).
        var exit = await SendAsync(shell, log, "exit configuration-mode", ct);
        if (exit.Contains("[yes,no]", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync(shell, log, "yes", ct);
        }

        return new CliResult(false, $"The switch refused the change: {error}", log.ToString());
    }
}
