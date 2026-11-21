namespace NetWatch.Core.Configuration;

/// <summary>
/// File-system layout of a NetWatch installation. Default root is C:\ProgramData\NetWatch.
/// </summary>
public sealed class NetWatchPaths
{
    public NetWatchPaths(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root must be set.", nameof(dataRoot));
        }

        Root = Path.GetFullPath(dataRoot);
    }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetWatch");

    public string Root { get; }
    public string DataDir => Path.Combine(Root, "data");
    public string LogsDir => Path.Combine(Root, "logs");
    public string CertsDir => Path.Combine(Root, "certs");
    public string BackupsDir => Path.Combine(Root, "backups");
    public string ReportsDir => Path.Combine(Root, "reports");

    /// <summary>Notification scripts. Only scripts in this folder can be run on alerts.</summary>
    public string ScriptsDir => Path.Combine(Root, "scripts");

    public string ConfigDb => Path.Combine(DataDir, "netwatch.db");
    public string MetricsDb => Path.Combine(DataDir, "metrics.db");
    public string EventsDb => Path.Combine(DataDir, "events.db");
    public string SettingsFile => Path.Combine(Root, "appsettings.json");
    public string InitialAdminPasswordFile => Path.Combine(Root, "initial-admin-password.txt");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, DataDir, LogsDir, CertsDir, BackupsDir, ReportsDir, ScriptsDir })
        {
            Directory.CreateDirectory(dir);
        }
    }
}

/// <summary>The HTTPS certificate the web console uses (for self-monitoring of its expiry).</summary>
public sealed record ServerCertificateInfo(string? Subject, DateTime? NotAfterUtc, string? Thumbprint);
