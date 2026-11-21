using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Core.Configuration;
using NetWatch.Data.Config;

namespace NetWatch.Data.Backup;

/// <summary>Contents description stored as <c>manifest.json</c> in every backup file.</summary>
public sealed record BackupManifest(
    string Product,
    string Version,
    DateTime CreatedUtc,
    string MachineName,
    bool IncludesMetrics,
    string Reason,
    IReadOnlyList<BackupFileEntry> Files);

public sealed record BackupFileEntry(string Path, long Size, string Sha256);

public sealed record BackupInfo(string Name, long SizeBytes, DateTime CreatedUtc, bool IncludesMetrics, string? Version, string? Reason);

/// <summary>
/// Consistent online backups: each SQLite database is copied with <c>VACUUM INTO</c> (a transactionally
/// consistent snapshot while the service keeps running), together with <c>appsettings.json</c> and the
/// certificate folder, into one zip with a SHA-256 manifest. Runs daily at the configured time (PKT) and on
/// demand; keeps the newest N files. Restore is offline only (<see cref="BackupRestore"/>).
/// </summary>
public sealed partial class BackupService : BackgroundService
{
    public const string FilePrefix = "netwatch-backup-";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly NetWatchPaths _paths;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupService(NetWatchPaths paths, SettingsService settings, TimeProvider time, ILogger<BackupService> logger)
    {
        _paths = paths;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    /// <summary>Last automatic or manual run (self-monitoring and the UI).</summary>
    public DateTime? LastAttemptUtc { get; private set; }
    public string? LastError { get; private set; }
    public bool Running => _gate.CurrentCount == 0;

    [GeneratedRegex(@"^netwatch-backup-\d{8}-\d{6}(-[a-z]+)?\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    public static bool IsValidName(string? name) => name is not null && FileNamePattern().IsMatch(name);

    public string TargetFolder => _settings.Backup.Folder ?? _paths.BackupsDir;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = NextRunUtc(_settings.Backup, _time.GetUtcNow().UtcDateTime);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (now >= next)
            {
                if (_settings.Backup.Enabled)
                {
                    try
                    {
                        await CreateAsync("scheduled", stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Already logged and recorded in LastError.
                    }
                }

                next = NextRunUtc(_settings.Backup, _time.GetUtcNow().UtcDateTime);
                continue;
            }

            // Follow a changed backup time; wake at least every 10 minutes to notice it.
            next = NextRunUtc(_settings.Backup, now);
            var wait = next - now;
            try
            {
                await Task.Delay(wait > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : wait, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Next daily run after <paramref name="afterUtc"/> at <see cref="BackupSettings.DailyAt"/> (PKT).</summary>
    public static DateTime NextRunUtc(BackupSettings s, DateTime afterUtc)
    {
        var at = TimeSpan.TryParseExact(s.DailyAt, @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(1, 30, 0);
        var local = LocalTime.ToLocal(afterUtc);
        var candidate = local.Date + at;
        if (candidate <= local)
        {
            candidate = candidate.AddDays(1);
        }

        return LocalTime.ToUtc(candidate);
    }

    /// <summary>Creates a backup now. Only one runs at a time; a second call fails fast.</summary>
    public async Task<BackupInfo> CreateAsync(string reason, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            throw new InvalidOperationException("A backup is already running.");
        }

        var s = _settings.Backup;
        var started = _time.GetUtcNow().UtcDateTime;
        LastAttemptUtc = started;
        var folder = TargetFolder;
        var stamp = LocalTime.ToLocal(started).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = $"{FilePrefix}{stamp}{(reason == "manual" ? "-manual" : reason == "pre-restore" ? "-prerestore" : string.Empty)}.zip";
        var work = Path.Combine(folder, $".tmp-{stamp}-{Guid.NewGuid():N}");
        try
        {
            if (s.ValidateFolder() is { } folderError)
            {
                throw new InvalidOperationException(folderError);
            }

            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(work, "data"));
            var files = new List<BackupFileEntry>();

            await Task.Run(() =>
            {
                Snapshot(_paths.ConfigDb, Path.Combine(work, "data", "netwatch.db"));
                Snapshot(_paths.EventsDb, Path.Combine(work, "data", "events.db"));
                if (s.IncludeMetrics)
                {
                    Snapshot(_paths.MetricsDb, Path.Combine(work, "data", "metrics.db"));
                }

                if (File.Exists(_paths.SettingsFile))
                {
                    File.Copy(_paths.SettingsFile, Path.Combine(work, "appsettings.json"));
                }

                if (Directory.Exists(_paths.CertsDir))
                {
                    Directory.CreateDirectory(Path.Combine(work, "certs"));
                    foreach (var f in Directory.EnumerateFiles(_paths.CertsDir))
                    {
                        File.Copy(f, Path.Combine(work, "certs", Path.GetFileName(f)));
                    }
                }

                foreach (var f in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories))
                {
                    files.Add(new BackupFileEntry(Path.GetRelativePath(work, f).Replace('\\', '/'), new FileInfo(f).Length, Hash(f)));
                }

                var manifest = new BackupManifest("NetWatch", Version(), started, Environment.MachineName, s.IncludeMetrics, reason, files);
                File.WriteAllText(Path.Combine(work, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));

                var target = Path.Combine(folder, name);
                var partial = target + ".partial";
                ZipFile.CreateFromDirectory(work, partial, CompressionLevel.Optimal, includeBaseDirectory: false);
                File.Move(partial, target, overwrite: true);
            }, ct);

            Prune(folder, s.KeepCount);
            LastError = null;
            var info = Read(Path.Combine(folder, name));
            _logger.LogInformation("Backup {Name} created ({Size:N0} bytes, {Reason})", name, info?.SizeBytes, reason);
            return info ?? new BackupInfo(name, 0, started, s.IncludeMetrics, Version(), reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Backup to {Folder} failed", folder);
            throw;
        }
        finally
        {
            TryDeleteDirectory(work);
            _gate.Release();
        }
    }

    /// <summary>Backups in the target folder, newest first.</summary>
    public IReadOnlyList<BackupInfo> List()
    {
        var folder = TargetFolder;
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, FilePrefix + "*.zip")
            .Where(f => IsValidName(Path.GetFileName(f)))
            .Select(Read)
            .Where(b => b is not null)
            .Select(b => b!)
            .OrderByDescending(b => b.CreatedUtc)
            .ToList();
    }

    /// <summary>Full path of a backup file, or null when the name is not a backup in the target folder.</summary>
    public string? PathOf(string name)
    {
        if (!IsValidName(name))
        {
            return null;
        }

        var path = Path.Combine(TargetFolder, name);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string name)
    {
        if (PathOf(name) is not { } path)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>Newest successful backup time (self-monitoring), or null.</summary>
    public DateTime? NewestUtc() => List().FirstOrDefault()?.CreatedUtc;

    internal static BackupInfo? Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("manifest.json");
            if (entry is null || entry.Length > 1_000_000)
            {
                return null;
            }

            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, JsonOptions);
            return manifest is null
                ? null
                : new BackupInfo(Path.GetFileName(path), new FileInfo(path).Length, manifest.CreatedUtc, manifest.IncludesMetrics, manifest.Version, manifest.Reason);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Snapshot(string source, string target)
    {
        if (!File.Exists(source))
        {
            return;
        }

        // VACUUM INTO writes a consistent, compacted copy while other connections keep reading and writing.
        using var connection = new SqliteConnection(SqliteConnectionFactory.Build(source));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target";
        command.Parameters.AddWithValue("$target", target);
        command.CommandTimeout = 3600;
        command.ExecuteNonQuery();
    }

    private void Prune(string folder, int keep)
    {
        var old = Directory.EnumerateFiles(folder, FilePrefix + "*.zip")
            .Where(f => IsValidName(Path.GetFileName(f)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(keep)
            .ToList();
        foreach (var f in old)
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete old backup {File}", f);
            }
        }

        // Leftovers of interrupted runs.
        foreach (var dir in Directory.EnumerateDirectories(folder, ".tmp-*"))
        {
            if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddHours(-6))
            {
                TryDeleteDirectory(dir);
            }
        }
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Version() =>
        typeof(BackupService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0";
}

/// <summary>
/// Offline restore (command line <c>--restore</c>, service stopped). Verifies the manifest and every file's
/// SHA-256, refuses paths outside the allow-list, moves the current files to <c>before-restore-…</c> and puts
/// the backup in place. Without metrics in the backup the current performance history is kept.
/// </summary>
public static class BackupRestore
{
    private static readonly string[] Allowed = ["data/netwatch.db", "data/events.db", "data/metrics.db", "appsettings.json", "manifest.json"];

    public static string Restore(string zipPath, NetWatchPaths paths)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("Backup file not found.", zipPath);
        }

        var stage = Path.Combine(paths.Root, $".restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            BackupManifest manifest;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Not a NetWatch backup (manifest.json missing).");
                using (var s = manifestEntry.Open())
                {
                    manifest = JsonSerializer.Deserialize<BackupManifest>(s, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new InvalidDataException("The backup manifest is unreadable.");
                }

                if (manifest.Product != "NetWatch" || manifest.Files.All(f => f.Path != "data/netwatch.db"))
                {
                    throw new InvalidDataException("Not a NetWatch backup (configuration database missing).");
                }

                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (name is "data/" or "certs/")
                    {
                        continue; // directory entries (e.g. an empty certs folder)
                    }

                    var allowed = Allowed.Contains(name) || (name.StartsWith("certs/", StringComparison.Ordinal) && name.Count(c => c == '/') == 1 && name.Length > 6);
                    var target = Path.GetFullPath(Path.Combine(stage, name));
                    if (!allowed || !target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"The backup contains an unexpected entry: {name}");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                }
            }

            foreach (var f in manifest.Files)
            {
                var path = Path.Combine(stage, f.Path);
                if (!File.Exists(path) || !string.Equals(BackupService.Hash(path), f.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Checksum mismatch for {f.Path}: the backup is damaged.");
                }
            }

            // Keep the current state next to the data folder, then move the backup in.
            var keep = Path.Combine(paths.Root, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(Path.Combine(keep, "data"));
            foreach (var db in new[] { "netwatch.db", "events.db", "metrics.db" })
            {
                var staged = Path.Combine(stage, "data", db);
                if (!File.Exists(staged))
                {
                    continue; // e.g. no metrics in the backup: keep the current history
                }

                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    var current = Path.Combine(paths.DataDir, db + suffix);
                    if (File.Exists(current))
                    {
                        File.Move(current, Path.Combine(keep, "data", db + suffix));
                    }
                }

                File.Move(staged, Path.Combine(paths.DataDir, db));
            }

            var stagedSettings = Path.Combine(stage, "appsettings.json");
            if (File.Exists(stagedSettings))
            {
                if (File.Exists(paths.SettingsFile))
                {
                    File.Move(paths.SettingsFile, Path.Combine(keep, "appsettings.json"));
                }

                File.Move(stagedSettings, paths.SettingsFile);
            }

            var stagedCerts = Path.Combine(stage, "certs");
            if (Directory.Exists(stagedCerts) && Directory.EnumerateFiles(stagedCerts).Any())
            {
                if (Directory.Exists(paths.CertsDir))
                {
                    Directory.Move(paths.CertsDir, Path.Combine(keep, "certs"));
                }

                Directory.Move(stagedCerts, paths.CertsDir);
            }

            return string.Create(CultureInfo.InvariantCulture,
                $"Restored backup of {LocalTime.ToLocal(manifest.CreatedUtc):yyyy-MM-dd HH:mm} PKT (NetWatch {manifest.Version}, from {manifest.MachineName}{(manifest.IncludesMetrics ? string.Empty : ", without performance history")}). The previous files were moved to {keep}.")
                + (string.Equals(manifest.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : " The backup comes from another server: delete the certs folder to get a new certificate, and re-enter all credential secrets (they are encrypted for the original server).");
        }
        finally
        {
            try
            {
                Directory.Delete(stage, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
