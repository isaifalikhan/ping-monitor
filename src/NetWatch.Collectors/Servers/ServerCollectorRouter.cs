using NetWatch.Collectors.Servers.Linux;
using NetWatch.Collectors.Servers.Windows;
using NetWatch.Core;

namespace NetWatch.Collectors.Servers;

/// <summary>Chooses the server collector from the credential kind: Windows → WinRM/WMI, SSH → Linux.</summary>
public sealed class ServerCollectorRouter : IServerCollector
{
    private readonly WindowsServerCollector _windows;
    private readonly SshServerCollector _ssh;

    public ServerCollectorRouter(WindowsServerCollector windows, SshServerCollector ssh)
    {
        _windows = windows;
        _ssh = ssh;
    }

    public Task<ServerPollData> CollectAsync(ServerPollRequest request, CancellationToken ct) => request.Credential?.Kind switch
    {
        CredentialKind.Windows => _windows.CollectAsync(request, ct),
        CredentialKind.Ssh => _ssh.CollectAsync(request, ct),
        _ => Task.FromResult(new ServerPollData { Error = "No Windows or SSH credential assigned." }),
    };
}
