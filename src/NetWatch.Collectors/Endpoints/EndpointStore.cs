using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Data;

namespace NetWatch.Collectors.Endpoints;

/// <summary>One enrolled machine (summary columns; the full latest report is stored compressed).</summary>
public sealed record EndpointRow(
    int Id,
    string MachineId,
    int? DeviceId,
    string Name,
    string? Domain,
    string? Fqdn,
    string? TokenHash,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    string? LastIp,
    string? ScriptVersion,
    string? Os,
    string? OsBuild,
    string? Model,
    string? Serial,
    string? User,
    string? FirewallOff,
    string? AvProblem,
    int? MissingUpdates,
    DateTime? LastPatchUtc,
    bool? PendingReboot,
    double? WorstDiskPct,
    int ProcessCount,
    int SoftwareCount,
    string? Errors,
    int? FileCount = null);

/// <summary>A file as stored for one machine.</summary>
public sealed record EndpointFileRow(int EndpointId, string Path, string? User, string? Folder, long Size, DateTime? ModifiedUtc, DateTime? CreatedUtc, string? Sha256);

/// <summary>What to change in a machine's file table (computed by <see cref="EndpointDiff.Files"/>).</summary>
public sealed record FileDelta(IReadOnlyList<EpFile> Upserts, IReadOnlyList<string> Removed, int Total);

/// <summary>A change found between two inventories of one machine.</summary>
public sealed record EndpointChange(long Id, int EndpointId, DateTime TimestampUtc, string Kind, string Item, string? Detail);

/// <summary>Summary values written with each report.</summary>
public sealed record EndpointSummary(
    string Name,
    string? Domain,
    string? Fqdn,
    string? Ip,
    string? ScriptVersion,
    string? Os,
    string? OsBuild,
    string? Model,
    string? Serial,
    string? User,
    string? FirewallOff,
    string? AvProblem,
    int? MissingUpdates,
    DateTime? LastPatchUtc,
    bool? PendingReboot,
    double? WorstDiskPct,
    string? Errors);

/// <summary>
/// endpoints.db: enrolled machines, their latest inventory (compressed JSON), searchable software and process
/// tables, and the change history. Kept apart from netwatch.db because every machine rewrites hundreds of rows
/// every 30 minutes. It is not part of the backup: after a restore the machines simply enroll again.
/// </summary>
public sealed class EndpointStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private const string KeyMeta = "enrollment-key";
    private readonly string _connectionString;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _write = new(1, 1);

    public EndpointStore(NetWatchPaths paths, ISecretProtector protector)
        : this(Path.Combine(paths.DataDir, "endpoints.db"), protector)
    {
    }

    internal EndpointStore(string path, ISecretProtector protector)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = SqliteConnectionFactory.Build(path);
        _protector = protector;
        SqliteConnectionFactory.ConfigureDatabaseFile(path);
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value BLOB);
            CREATE TABLE IF NOT EXISTS endpoints(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                machine_id TEXT NOT NULL UNIQUE COLLATE NOCASE,
                device_id INTEGER,
                name TEXT NOT NULL COLLATE NOCASE,
                domain TEXT COLLATE NOCASE,
                fqdn TEXT,
                token_hash TEXT,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                last_ip TEXT,
                script_version TEXT,
                os TEXT, os_build TEXT, model TEXT, serial TEXT, user_name TEXT,
                firewall_off TEXT, av_problem TEXT,
                missing_updates INTEGER, last_patch TEXT, pending_reboot INTEGER,
                worst_disk_pct REAL,
                process_count INTEGER NOT NULL DEFAULT 0,
                software_count INTEGER NOT NULL DEFAULT 0,
                errors TEXT,
                report BLOB);
            CREATE INDEX IF NOT EXISTS ix_endpoints_device ON endpoints(device_id);
            CREATE INDEX IF NOT EXISTS ix_endpoints_name ON endpoints(name);
            CREATE TABLE IF NOT EXISTS software(
                endpoint_id INTEGER NOT NULL, name TEXT NOT NULL COLLATE NOCASE, version TEXT, publisher TEXT, install_date TEXT);
            CREATE INDEX IF NOT EXISTS ix_software_name ON software(name);
            CREATE INDEX IF NOT EXISTS ix_software_endpoint ON software(endpoint_id);
            CREATE TABLE IF NOT EXISTS processes(
                endpoint_id INTEGER NOT NULL, name TEXT NOT NULL COLLATE NOCASE, path TEXT, user_name TEXT, pid INTEGER, command_line TEXT);
            CREATE INDEX IF NOT EXISTS ix_processes_name ON processes(name);
            CREATE INDEX IF NOT EXISTS ix_processes_endpoint ON processes(endpoint_id);
            CREATE TABLE IF NOT EXISTS changes(
                id INTEGER PRIMARY KEY AUTOINCREMENT, endpoint_id INTEGER NOT NULL, ts TEXT NOT NULL,
                kind TEXT NOT NULL, item TEXT NOT NULL, detail TEXT);
            CREATE INDEX IF NOT EXISTS ix_changes_ts ON changes(ts);
            CREATE INDEX IF NOT EXISTS ix_changes_endpoint ON changes(endpoint_id, ts);
            CREATE TABLE IF NOT EXISTS files(
                endpoint_id INTEGER NOT NULL, path TEXT NOT NULL COLLATE NOCASE, name TEXT NOT NULL COLLATE NOCASE,
                ext TEXT COLLATE NOCASE, user_name TEXT, folder TEXT, size INTEGER NOT NULL, modified TEXT, created TEXT, sha256 TEXT,
                PRIMARY KEY(endpoint_id, path));
            CREATE INDEX IF NOT EXISTS ix_files_name ON files(name);
            CREATE INDEX IF NOT EXISTS ix_files_ext ON files(ext);
            CREATE INDEX IF NOT EXISTS ix_files_sha ON files(sha256);
            """);

        // 2.0.2 → later: file list per machine. NULL = no file list received yet (the first one is the baseline).
        if (!Columns(c, "endpoints").Contains("file_count"))
        {
            Exec(c, "ALTER TABLE endpoints ADD COLUMN file_count INTEGER");
        }
    }

    private static HashSet<string> Columns(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            set.Add(r.GetString(1));
        }

        return set;
    }

    // ---------------------------------------------------------------- enrollment key and tokens

    /// <summary>The enrollment key written into the deployment kit; created on first use.</summary>
    public string EnrollmentKey()
    {
        using var c = Open();
        if (Scalar(c, "SELECT value FROM meta WHERE key = $k", ("$k", KeyMeta)) is byte[] stored)
        {
            return _protector.Unprotect(stored);
        }

        return RotateEnrollmentKey();
    }

    /// <summary>Creates a new enrollment key. Enrolled machines keep working with their own token.</summary>
    public string RotateEnrollmentKey()
    {
        var key = NewSecret(24);
        using var c = Open();
        Exec(c, "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$k", KeyMeta), ("$v", _protector.Protect(key)));
        return key;
    }

    public bool IsEnrollmentKey(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(EnrollmentKey()));
    }

    public static string NewSecret(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool TokenMatches(string? token, string? hash) =>
        !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(hash) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashToken(token)), Encoding.ASCII.GetBytes(hash));

    // ---------------------------------------------------------------- reads

    public EndpointRow? FindByMachine(string machineId)
    {
        using var c = Open();
        return Rows(c, "SELECT * FROM endpoints WHERE machine_id = $m", ("$m", machineId)).FirstOrDefault();
    }

    public EndpointRow? Get(int id)
    {
        using var c = Open();
        return Rows(c, "SELECT * FROM endpoints WHERE id = $id", ("$id", id)).FirstOrDefault();
    }

    /// <summary>Other enrolled machines with this computer name in the same domain.</summary>
    public List<EndpointRow> FindByName(string name, string? domain, string exceptMachineId)
    {
        using var c = Open();
        return Rows(c, "SELECT * FROM endpoints WHERE name = $n AND IFNULL(domain, '') = $d AND machine_id <> $m",
            ("$n", name), ("$d", domain ?? string.Empty), ("$m", exceptMachineId));
    }

    public List<EndpointRow> All()
    {
        using var c = Open();
        return Rows(c, "SELECT * FROM endpoints ORDER BY name");
    }

    public EndpointRow? ForDevice(int deviceId)
    {
        using var c = Open();
        return Rows(c, "SELECT * FROM endpoints WHERE device_id = $d ORDER BY last_seen DESC LIMIT 1", ("$d", deviceId)).FirstOrDefault();
    }

    /// <summary>The latest full inventory of a machine, or null.</summary>
    public EndpointReport? Report(int id)
    {
        using var c = Open();
        return Scalar(c, "SELECT report FROM endpoints WHERE id = $id", ("$id", id)) is byte[] blob ? Decode(blob) : null;
    }

    /// <summary>Installed programs across all machines, grouped by name (machine count and versions).</summary>
    public List<(string Name, string? Publisher, int Machines, string Versions)> SoftwareSummary(string? term, int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT name, MAX(publisher), COUNT(DISTINCT endpoint_id), GROUP_CONCAT(DISTINCT version)
            FROM software WHERE ($t IS NULL OR name LIKE $t ESCAPE '\' OR publisher LIKE $t ESCAPE '\')
            GROUP BY name ORDER BY COUNT(DISTINCT endpoint_id) DESC, name LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$t", Like(term) ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<(string, string?, int, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? string.Empty : r.GetString(3)));
        }

        return list;
    }

    /// <summary>Machines that have a program (exact name) installed, with the version.</summary>
    public List<(int EndpointId, string Version)> SoftwareMachines(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT endpoint_id, IFNULL(version, '') FROM software WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", name);
        var list = new List<(int, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetInt32(0), r.GetString(1)));
        }

        return list;
    }

    /// <summary>Running processes matching a name, path or command line across all machines.</summary>
    public List<(int EndpointId, string Name, string? Path, string? User, int Pid, string? CommandLine)> SearchProcesses(string term, int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT endpoint_id, name, path, user_name, pid, command_line FROM processes
            WHERE name LIKE $t ESCAPE '\' OR path LIKE $t ESCAPE '\' OR command_line LIKE $t ESCAPE '\'
            ORDER BY name LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$t", Like(term)!);
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<(int, string, string?, string?, int, string?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetInt32(0), r.GetString(1), Str(r, 2), Str(r, 3), r.IsDBNull(4) ? 0 : r.GetInt32(4), Str(r, 5)));
        }

        return list;
    }

    public List<EndpointChange> Changes(int? endpointId, DateTime sinceUtc, string? kind, int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, endpoint_id, ts, kind, item, detail FROM changes
            WHERE ts >= $s AND ($e IS NULL OR endpoint_id = $e) AND ($k IS NULL OR kind = $k)
            ORDER BY ts DESC, id DESC LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$s", Ts(sinceUtc));
        cmd.Parameters.AddWithValue("$e", endpointId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$k", kind ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<EndpointChange>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EndpointChange(r.GetInt64(0), r.GetInt32(1), ParseTs(r.GetString(2)), r.GetString(3), r.GetString(4), Str(r, 5)));
        }

        return list;
    }

    // ---------------------------------------------------------------- writes

    /// <summary>Creates the row of a newly enrolled machine and returns its ID.</summary>
    public async Task<int> EnrollAsync(string machineId, string name, string? domain, string tokenHash, DateTime nowUtc)
    {
        await _write.WaitAsync();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO endpoints(machine_id, name, domain, token_hash, first_seen, last_seen)
                VALUES ($m, $n, $d, $t, $now, $now)
                ON CONFLICT(machine_id) DO UPDATE SET token_hash = excluded.token_hash, name = excluded.name, domain = excluded.domain
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("$m", machineId);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$d", domain ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$t", tokenHash);
            cmd.Parameters.AddWithValue("$now", Ts(nowUtc));
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>The stored file list of a machine, by path (for comparing with a new list).</summary>
    public Dictionary<string, EndpointFileRow> FileState(int endpointId)
    {
        using var c = Open();
        var d = new Dictionary<string, EndpointFileRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in FileRows(c, "WHERE endpoint_id = $e", ("$e", endpointId)))
        {
            d[f.Path] = f;
        }

        return d;
    }

    /// <summary>Files of one machine, optionally filtered by text (path) and folder.</summary>
    public List<EndpointFileRow> Files(int endpointId, string? term, string? folder, int limit)
    {
        using var c = Open();
        return FileRows(c, "WHERE endpoint_id = $e AND ($t IS NULL OR path LIKE $t ESCAPE '\\') AND ($f IS NULL OR folder = $f) ORDER BY path LIMIT $l",
            ("$e", endpointId), ("$t", Like(term)), ("$f", string.IsNullOrWhiteSpace(folder) ? null : folder), ("$l", limit));
    }

    /// <summary>Files across all machines by name/path text, extension, exact SHA-256 and minimum size.</summary>
    public List<EndpointFileRow> SearchFiles(string? term, string? ext, string? sha256, long? minBytes, string? folder, int limit)
    {
        using var c = Open();
        return FileRows(c, """
            WHERE ($t IS NULL OR path LIKE $t ESCAPE '\') AND ($x IS NULL OR ext = $x) AND ($h IS NULL OR sha256 = $h)
              AND ($m IS NULL OR size >= $m) AND ($f IS NULL OR folder = $f)
            ORDER BY size DESC LIMIT $l
            """,
            ("$t", Like(term)), ("$x", string.IsNullOrWhiteSpace(ext) ? null : ext.Trim().TrimStart('.').ToLowerInvariant()),
            ("$h", string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToUpperInvariant()), ("$m", minBytes),
            ("$f", string.IsNullOrWhiteSpace(folder) ? null : folder), ("$l", limit));
    }

    private static List<EndpointFileRow> FileRows(SqliteConnection c, string where, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT endpoint_id, path, user_name, folder, size, modified, created, sha256 FROM files " + where;
        Add(cmd, args);
        var list = new List<EndpointFileRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EndpointFileRow(r.GetInt32(0), r.GetString(1), Str(r, 2), Str(r, 3), r.GetInt64(4),
                r.IsDBNull(5) ? null : ParseTs(r.GetString(5)), r.IsDBNull(6) ? null : ParseTs(r.GetString(6)), Str(r, 7)));
        }

        return list;
    }

    /// <summary>File extension as stored ("exe"; "" for none).</summary>
    public static string Extension(string path)
    {
        var name = System.IO.Path.GetFileName(path.Replace('/', '\\').Split('\\')[^1]);
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 || name.Length - dot > 12 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>Stores a report: summary columns, the compressed document, software/process rows, file changes and the changes found.</summary>
    public async Task SaveReportAsync(int id, int? deviceId, EndpointReport report, EndpointSummary s, IReadOnlyList<(string Kind, string Item, string? Detail)> changes, DateTime nowUtc, FileDelta? files = null)
    {
        // The file list lives in the files table; the compressed report keeps everything else.
        var fileList = report.Files;
        report.Files = new List<EpFile>();
        byte[] blob;
        try
        {
            blob = Encode(report);
        }
        finally
        {
            report.Files = fileList;
        }

        await _write.WaitAsync();
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            if (files is not null)
            {
                ApplyFiles(c, tx, id, files);
            }

            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE endpoints SET device_id = $dev, name = $n, domain = $d, fqdn = $f, last_seen = $now, last_ip = $ip,
                        script_version = $sv, os = $os, os_build = $ob, model = $mo, serial = $se, user_name = $u,
                        firewall_off = $fw, av_problem = $av, missing_updates = $mu, last_patch = $lp, pending_reboot = $pr,
                        worst_disk_pct = $wd, process_count = $pc, software_count = $sc, errors = $er, report = $rep
                    WHERE id = $id
                    """;
                Add(cmd, ("$id", id), ("$dev", deviceId), ("$n", s.Name), ("$d", s.Domain), ("$f", s.Fqdn), ("$now", Ts(nowUtc)), ("$ip", s.Ip),
                    ("$sv", s.ScriptVersion), ("$os", s.Os), ("$ob", s.OsBuild), ("$mo", s.Model), ("$se", s.Serial), ("$u", s.User),
                    ("$fw", s.FirewallOff), ("$av", s.AvProblem), ("$mu", s.MissingUpdates), ("$lp", s.LastPatchUtc is { } lp ? Ts(lp) : null),
                    ("$pr", s.PendingReboot is { } pr ? (pr ? 1 : 0) : null), ("$wd", s.WorstDiskPct), ("$pc", report.Processes.Count),
                    ("$sc", report.Software.Count), ("$er", s.Errors), ("$rep", blob));
                cmd.ExecuteNonQuery();
            }

            Exec(c, tx, "DELETE FROM software WHERE endpoint_id = $id", ("$id", id));
            Exec(c, tx, "DELETE FROM processes WHERE endpoint_id = $id", ("$id", id));
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO software(endpoint_id, name, version, publisher, install_date) VALUES ($id, $n, $v, $p, $d)";
                var pn = Param(cmd, "$n");
                var pv = Param(cmd, "$v");
                var pp = Param(cmd, "$p");
                var pd = Param(cmd, "$d");
                cmd.Parameters.AddWithValue("$id", id);
                foreach (var sw in report.Software.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
                {
                    pn.Value = sw.Name!.Trim();
                    pv.Value = (object?)sw.Version ?? DBNull.Value;
                    pp.Value = (object?)sw.Publisher ?? DBNull.Value;
                    pd.Value = (object?)sw.InstallDate ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }

            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO processes(endpoint_id, name, path, user_name, pid, command_line) VALUES ($id, $n, $p, $u, $pid, $cl)";
                var pn = Param(cmd, "$n");
                var pp = Param(cmd, "$p");
                var pu = Param(cmd, "$u");
                var ppid = Param(cmd, "$pid");
                var pcl = Param(cmd, "$cl");
                cmd.Parameters.AddWithValue("$id", id);
                foreach (var p in report.Processes.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
                {
                    pn.Value = p.Name!;
                    pp.Value = (object?)p.Path ?? DBNull.Value;
                    pu.Value = (object?)p.User ?? DBNull.Value;
                    ppid.Value = p.Pid;
                    pcl.Value = (object?)p.CommandLine ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }

            if (changes.Count > 0)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO changes(endpoint_id, ts, kind, item, detail) VALUES ($id, $ts, $k, $i, $d)";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$ts", Ts(nowUtc));
                var pk = Param(cmd, "$k");
                var pi = Param(cmd, "$i");
                var pd = Param(cmd, "$d");
                foreach (var (kind, item, detail) in changes)
                {
                    pk.Value = kind;
                    pi.Value = Cut(item, 500);
                    pd.Value = (object?)Cut(detail, 1000) ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
        finally
        {
            _write.Release();
        }
    }

    private static void ApplyFiles(SqliteConnection c, SqliteTransaction tx, int id, FileDelta files)
    {
        if (files.Removed.Count > 0)
        {
            using var del = c.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM files WHERE endpoint_id = $id AND path = $p";
            del.Parameters.AddWithValue("$id", id);
            var pp = Param(del, "$p");
            foreach (var path in files.Removed)
            {
                pp.Value = path;
                del.ExecuteNonQuery();
            }
        }

        if (files.Upserts.Count > 0)
        {
            using var up = c.CreateCommand();
            up.Transaction = tx;
            up.CommandText = """
                INSERT INTO files(endpoint_id, path, name, ext, user_name, folder, size, modified, created, sha256)
                VALUES ($id, $p, $n, $x, $u, $f, $s, $m, $c, $h)
                ON CONFLICT(endpoint_id, path) DO UPDATE SET name = excluded.name, ext = excluded.ext, user_name = excluded.user_name,
                    folder = excluded.folder, size = excluded.size, modified = excluded.modified, created = excluded.created, sha256 = excluded.sha256
                """;
            up.Parameters.AddWithValue("$id", id);
            var (pp, pn, px, pu, pf, ps, pm, pc, ph) = (Param(up, "$p"), Param(up, "$n"), Param(up, "$x"), Param(up, "$u"), Param(up, "$f"), Param(up, "$s"), Param(up, "$m"), Param(up, "$c"), Param(up, "$h"));
            foreach (var f in files.Upserts)
            {
                pp.Value = f.Path!;
                pn.Value = System.IO.Path.GetFileName(f.Path!.Replace('/', '\\').Split('\\')[^1]);
                px.Value = Extension(f.Path);
                pu.Value = (object?)f.User ?? DBNull.Value;
                pf.Value = (object?)f.Folder ?? DBNull.Value;
                ps.Value = f.Size;
                pm.Value = f.ModifiedUtc is { } m ? Ts(m) : DBNull.Value;
                pc.Value = f.CreatedUtc is { } cr ? Ts(cr) : DBNull.Value;
                ph.Value = (object?)f.Sha256?.ToUpperInvariant() ?? DBNull.Value;
                up.ExecuteNonQuery();
            }
        }

        Exec(c, tx, "UPDATE endpoints SET file_count = $n WHERE id = $id", ("$id", id), ("$n", files.Total));
    }

    /// <summary>Forgets the machine's token: its next report must carry the enrollment key again.</summary>
    public Task ResetTokenAsync(int id) => WriteAsync("UPDATE endpoints SET token_hash = NULL WHERE id = $id", ("$id", id));

    public async Task DeleteAsync(int id)
    {
        await _write.WaitAsync();
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            foreach (var table in new[] { "software", "processes", "changes", "files" })
            {
                Exec(c, tx, $"DELETE FROM {table} WHERE endpoint_id = $id", ("$id", id));
            }

            Exec(c, tx, "DELETE FROM endpoints WHERE id = $id", ("$id", id));
            tx.Commit();
        }
        finally
        {
            _write.Release();
        }
    }

    public Task PurgeChangesAsync(DateTime beforeUtc) => WriteAsync("DELETE FROM changes WHERE ts < $b", ("$b", Ts(beforeUtc)));

    // ---------------------------------------------------------------- helpers

    internal static byte[] Encode(EndpointReport report)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            JsonSerializer.Serialize(gz, report, Json);
        }

        return ms.ToArray();
    }

    internal static EndpointReport? Decode(byte[] blob)
    {
        try
        {
            using var gz = new GZipStream(new MemoryStream(blob), CompressionMode.Decompress);
            return JsonSerializer.Deserialize<EndpointReport>(gz, Json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    internal static string Ts(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTime ParseTs(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string? Like(string? term) =>
        string.IsNullOrWhiteSpace(term) ? null : "%" + term.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private async Task WriteAsync(string sql, params (string, object?)[] args)
    {
        await _write.WaitAsync();
        try
        {
            using var c = Open();
            Exec(c, sql, args);
        }
        finally
        {
            _write.Release();
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static SqliteParameter Param(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    private static void Add(SqliteCommand cmd, params (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args) => Exec(c, null, sql, args);

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Add(cmd, args);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Add(cmd, args);
        return cmd.ExecuteScalar();
    }

    private static List<EndpointRow> Rows(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql.Replace("SELECT *", "SELECT id, machine_id, device_id, name, domain, fqdn, token_hash, first_seen, last_seen, last_ip, script_version, os, os_build, model, serial, user_name, firewall_off, av_problem, missing_updates, last_patch, pending_reboot, worst_disk_pct, process_count, software_count, errors, file_count", StringComparison.Ordinal);
        Add(cmd, args);
        var list = new List<EndpointRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EndpointRow(
                r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3), Str(r, 4), Str(r, 5), Str(r, 6),
                ParseTs(r.GetString(7)), ParseTs(r.GetString(8)), Str(r, 9), Str(r, 10), Str(r, 11), Str(r, 12), Str(r, 13), Str(r, 14),
                Str(r, 15), Str(r, 16), Str(r, 17), r.IsDBNull(18) ? null : r.GetInt32(18), r.IsDBNull(19) ? null : ParseTs(r.GetString(19)),
                r.IsDBNull(20) ? null : r.GetInt32(20) != 0, r.IsDBNull(21) ? null : r.GetDouble(21), r.GetInt32(22), r.GetInt32(23), Str(r, 24), r.IsDBNull(25) ? null : r.GetInt32(25)));
        }

        return list;
    }
}
