using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;

namespace NetWatch.Data.Backup;

/// <summary>
/// Credential secrets are DPAPI-encrypted for the machine they were entered on, so a plain backup restored on a
/// new server has unreadable credentials. A portable export re-encrypts them with a key derived from an
/// administrator password (PBKDF2-SHA256, 600,000 iterations, random salt; AES-256-GCM per secret) and stores
/// them as <c>credentials.portable.json</c> in the backup. Restoring with the password re-protects them with the
/// new server's DPAPI. The password itself is never stored.
/// </summary>
public static class PortableSecrets
{
    public const string FileName = "credentials.portable.json";
    public const int Iterations = 600_000;
    public const int MinPasswordLength = 12;

    public sealed record Item(int Id, string Name, string Nonce, string Cipher, string Tag);

    public sealed record Document(string Format, string Kdf, int Iterations, string Salt, string Check, List<Item> Items);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Reads every credential of the live configuration database, decrypts it with DPAPI and re-encrypts it with the password.</summary>
    public static string Export(string configDb, ISecretProtector protector, string password)
    {
        Validate(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var items = new List<Item>();
        using (var connection = new SqliteConnection(Unpooled(configDb)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, SecretBlob FROM CredentialProfiles ORDER BY Id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var plain = protector.Unprotect((byte[])reader["SecretBlob"]);
                items.Add(Seal(key, reader.GetInt32(0), reader.GetString(1), plain));
            }
        }

        // A known value sealed with the key lets a restore verify the password before touching anything.
        var check = Seal(key, 0, "check", "NetWatch");
        return JsonSerializer.Serialize(new Document("netwatch-portable-credentials-1", "PBKDF2-SHA256", Iterations, Convert.ToBase64String(salt),
            $"{check.Nonce}.{check.Cipher}.{check.Tag}", items), Json);
    }

    /// <summary>Decrypts all items (throws <see cref="CryptographicException"/> for a wrong password or a damaged file).</summary>
    public static Dictionary<int, string> Decrypt(string json, string password)
    {
        var doc = JsonSerializer.Deserialize<Document>(json, Json) ?? throw new InvalidDataException("The portable credential file is unreadable.");
        if (doc.Format != "netwatch-portable-credentials-1" || doc.Iterations < 100_000)
        {
            throw new InvalidDataException("Unknown portable credential format.");
        }

        var key = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(doc.Salt), doc.Iterations, HashAlgorithmName.SHA256, 32);
        var parts = doc.Check.Split('.');
        try
        {
            if (parts.Length != 3 || Open(key, new Item(0, "check", parts[0], parts[1], parts[2])) != "NetWatch")
            {
                throw new CryptographicException("Wrong password for the portable credentials.");
            }
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException("Wrong password for the portable credentials.");
        }

        try
        {
            return doc.Items.ToDictionary(i => i.Id, i => Open(key, i));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException("The portable credential file is damaged (an entry failed its integrity check).");
        }
    }

    /// <summary>Writes the decrypted secrets into a (restored) configuration database, protected for this machine.</summary>
    public static int Apply(string configDb, IReadOnlyDictionary<int, string> secrets, ISecretProtector protector)
    {
        using var connection = new SqliteConnection(Unpooled(configDb));
        connection.Open();
        using var tx = connection.BeginTransaction();
        var updated = 0;
        foreach (var (id, plain) in secrets)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "UPDATE CredentialProfiles SET SecretBlob = $blob WHERE Id = $id";
            command.Parameters.AddWithValue("$blob", protector.Protect(plain));
            command.Parameters.AddWithValue("$id", id);
            updated += command.ExecuteNonQuery();
        }

        tx.Commit();
        return updated;
    }

    /// <summary>No pooling: nothing keeps the file open after the export/apply (restores move these files).</summary>
    private static string Unpooled(string file) => new SqliteConnectionStringBuilder(SqliteConnectionFactory.Build(file)) { Pooling = false }.ToString();

    public static void Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"The export password must have at least {MinPasswordLength} characters.");
        }
    }

    private static Item Seal(byte[] key, int id, string name, string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new Item(id, name, Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
    }

    private static string Open(byte[] key, Item item)
    {
        var cipher = Convert.FromBase64String(item.Cipher);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(Convert.FromBase64String(item.Nonce), cipher, Convert.FromBase64String(item.Tag), plain,
            Encoding.UTF8.GetBytes(item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>
/// A restore requested from the web console: the service writes this marker and stops; the next start restores
/// the backup before any database is opened. The optional portable-credential password is kept DPAPI-protected
/// in the marker only until the restore ran.
/// </summary>
public sealed record PendingRestore(string BackupPath, string RequestedBy, DateTime RequestedUtc, string? ProtectedPassword)
{
    public const string FileName = "restore-pending.json";

    public static string PathIn(NetWatchPaths paths) => Path.Combine(paths.Root, FileName);

    public void Save(NetWatchPaths paths) => File.WriteAllText(PathIn(paths), JsonSerializer.Serialize(this));

    public static PendingRestore? Load(NetWatchPaths paths) =>
        File.Exists(PathIn(paths)) ? JsonSerializer.Deserialize<PendingRestore>(File.ReadAllText(PathIn(paths))) : null;

    /// <summary>Runs a pending restore (start-up, before the databases are opened). Returns the outcome text, or null when none was pending.</summary>
    public static string? RunIfPending(NetWatchPaths paths, ISecretProtector protector)
    {
        var pending = Load(paths);
        if (pending is null)
        {
            return null;
        }

        File.Delete(PathIn(paths)); // never loop on a broken backup
        try
        {
            var password = pending.ProtectedPassword is { } p ? protector.Unprotect(Convert.FromBase64String(p)) : null;
            return BackupRestore.Restore(pending.BackupPath, paths, password, protector) + $" (requested by {pending.RequestedBy} at {pending.RequestedUtc:u}).";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException or ArgumentException)
        {
            return $"FAILED: the restore requested by {pending.RequestedBy} did not run, nothing was changed: {ex.Message}";
        }
    }
}
