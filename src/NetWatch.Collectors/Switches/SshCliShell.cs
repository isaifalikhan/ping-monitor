using System.Text;
using Renci.SshNet;

namespace NetWatch.Collectors.Switches;

/// <summary>An interactive SSH shell (like PuTTY) on a switch: reads until the CLI shows a prompt, pages through "--More--".</summary>
public sealed class SshCliShell : ICliShell
{
    private readonly ShellStream _stream;
    private readonly TimeSpan _timeout;

    public SshCliShell(ShellStream stream, TimeSpan timeout)
    {
        _stream = stream;
        _timeout = timeout;
    }

    public Task<string> ReadBannerAsync(CancellationToken ct) => ReadUntilPromptAsync(ct);

    public Task<string> SendAsync(string line, CancellationToken ct)
    {
        _stream.Write(line + "\r");
        _stream.Flush();
        return ReadUntilPromptAsync(ct);
    }

    private async Task<string> ReadUntilPromptAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        var deadline = DateTime.UtcNow + _timeout;
        var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_stream.DataAvailable)
            {
                var n = _stream.Read(buffer, 0, buffer.Length);
                if (n > 0)
                {
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    quietSince = DateTime.UtcNow;
                    continue;
                }
            }

            var text = sb.ToString();

            // Wait for a short quiet moment so a prompt-like line in the middle of output is not taken as the end.
            if (DateTime.UtcNow - quietSince > TimeSpan.FromMilliseconds(300))
            {
                if (SwitchCliCommands.AsksToPage(text))
                {
                    _stream.Write(" ");
                    _stream.Flush();
                    quietSince = DateTime.UtcNow;
                    continue;
                }

                if (SwitchCliCommands.EndsWithPrompt(text) || SwitchCliCommands.AsksForPassword(text) || text.Contains("[yes,no]", StringComparison.OrdinalIgnoreCase))
                {
                    return text;
                }
            }

            await Task.Delay(50, ct);
        }

        throw new TimeoutException("The switch did not show a prompt in time. Output so far: " + SwitchCliCommands.Clean(sb.ToString()).Trim());
    }
}
