using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using NetWatch.Core;
using NetWatch.Core.Configuration;

namespace NetWatch.Web.Auth;

/// <summary>Outcome of a directory sign-in.</summary>
/// <param name="DirectoryUnavailable">No domain controller could be reached (as opposed to wrong credentials).</param>
public sealed record LdapLoginResult(
    bool Success,
    UserRole? Role,
    string? SamAccountName,
    string? DisplayName,
    string? Email,
    string? Error,
    IReadOnlyList<string> MatchedGroups,
    bool DirectoryUnavailable = false);

/// <summary>
/// Authenticates users against Active Directory by binding as the user (no service account or stored directory
/// password is needed), then resolves the NetWatch role from nested membership of the mapped AD groups.
/// </summary>
public sealed class LdapAuthenticator
{
    private const int InvalidCredentials = 49;

    // LDAP_MATCHING_RULE_IN_CHAIN: matches nested group membership.
    private const string InChain = "1.2.840.113556.1.4.1941";

    private readonly ILogger<LdapAuthenticator> _logger;

    public LdapAuthenticator(ILogger<LdapAuthenticator> logger)
    {
        _logger = logger;
    }

    /// <summary>Reduces "DOMAIN\user" and "user@domain" to the sAMAccountName. Returns null for invalid input.</summary>
    public static string? NormalizeUsername(string input)
    {
        var name = input.Trim();
        var slash = name.IndexOf('\\');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var at = name.IndexOf('@');
        if (at >= 0)
        {
            name = name[..at];
        }

        // sAMAccountName: max 20 characters, none of " / \ [ ] : ; | = , + * ? < > (AD rules); also no control characters.
        return name.Length is > 0 and <= 64 && !name.Any(c => char.IsControl(c) || "\"/\\[]:;|=,+*?<>()".Contains(c)) ? name : null;
    }

    /// <summary>Escapes a value for use inside an LDAP search filter (RFC 4515).</summary>
    public static string EscapeFilter(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\5c"); break;
                case '*': sb.Append(@"\2a"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\0': sb.Append(@"\00"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Highest role among the mapped groups the user belongs to, or null.</summary>
    public static UserRole? ResolveRole(IEnumerable<LdapRoleMapping> mappings, IEnumerable<string> memberOfGroupDns)
    {
        var set = memberOfGroupDns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roles = mappings.Where(m => set.Contains(m.GroupDn.Trim())).Select(m => m.Role).ToList();
        return roles.Count == 0 ? null : roles.Max();
    }

    public Task<LdapLoginResult> AuthenticateAsync(LdapSettings settings, string username, string password, CancellationToken ct) =>
        Task.Run(() => Authenticate(settings, username, password), ct);

    private LdapLoginResult Authenticate(LdapSettings s, string username, string password)
    {
        if (s.Validate() is { } configError)
        {
            return Fail($"Directory sign-in is not configured correctly: {configError}");
        }

        var sam = NormalizeUsername(username);
        if (sam is null)
        {
            return Fail("Invalid user name.");
        }

        // An empty password would be an anonymous bind, which "succeeds" without checking anything.
        if (string.IsNullOrEmpty(password))
        {
            return Fail("Invalid user name or password.");
        }

        var unavailable = new List<string>();
        foreach (var server in s.Servers)
        {
            try
            {
                using var connection = Connect(s, server);
                if (s.BindMethod == LdapBindMethod.Negotiate)
                {
                    connection.AuthType = AuthType.Negotiate;
                    connection.Bind(new NetworkCredential(sam, password, s.Domain));
                }
                else
                {
                    connection.AuthType = AuthType.Basic;
                    connection.Bind(new NetworkCredential($"{sam}@{s.Domain}", password));
                }

                return Lookup(connection, s, sam);
            }
            catch (LdapException ex) when (ex.ErrorCode == InvalidCredentials)
            {
                // Wrong password, disabled/expired/locked AD account: the same message for all (no user enumeration).
                return Fail("Invalid user name or password.");
            }
            catch (Exception ex) when (ex is LdapException or DirectoryOperationException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "LDAP sign-in via {Server} failed", server);
                unavailable.Add($"{server}: {ex.Message}");
            }
        }

        return new LdapLoginResult(false, null, sam, null, null, $"No domain controller reachable ({string.Join("; ", unavailable)}).", Array.Empty<string>(), DirectoryUnavailable: true);
    }

    private static LdapConnection Connect(LdapSettings s, string server)
    {
        var id = new LdapDirectoryIdentifier(server, s.Port, fullyQualifiedDnsHostName: true, connectionless: false);
        var connection = new LdapConnection(id) { Timeout = TimeSpan.FromSeconds(s.TimeoutSec) };
        var options = connection.SessionOptions;
        options.ProtocolVersion = 3;
        options.ReferralChasing = ReferralChasingOptions.None;
        // Our own verification (chain under the air-gap TLS policy + host name) instead of the system one, which
        // may try to download CRLs and hang on an isolated network.
        options.VerifyServerCertificate = s.ValidateCertificate
            ? (connection, certificate) => TlsPolicy.Verify(certificate, server, out var error)
            : (_, _) => true;

        switch (s.Security)
        {
            case LdapSecurity.Ldaps:
                options.SecureSocketLayer = true;
                break;
            case LdapSecurity.StartTls:
                options.StartTransportLayerSecurity(null);
                break;
            default:
                // Plain LDAP is only allowed with Negotiate: protect the session with Kerberos/NTLM signing and sealing.
                options.Signing = true;
                options.Sealing = true;
                break;
        }

        return connection;
    }

    private static LdapLoginResult Lookup(LdapConnection connection, LdapSettings s, string sam)
    {
        var esc = EscapeFilter(sam);
        var user = (SearchResponse)connection.SendRequest(new SearchRequest(
            s.BaseDn,
            $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={esc}))",
            SearchScope.Subtree,
            "sAMAccountName", "displayName", "mail"));
        if (user.Entries.Count != 1)
        {
            return new LdapLoginResult(false, null, sam, null, null, "The account was not found below the configured search base.", Array.Empty<string>());
        }

        var entry = user.Entries[0];
        var matched = new List<string>();
        foreach (var mapping in s.RoleMappings)
        {
            var groupDn = mapping.GroupDn.Trim();
            var member = (SearchResponse)connection.SendRequest(new SearchRequest(
                s.BaseDn,
                $"(&(sAMAccountName={esc})(memberOf:{InChain}:={EscapeFilter(groupDn)}))",
                SearchScope.Subtree,
                "sAMAccountName"));
            if (member.Entries.Count > 0)
            {
                matched.Add(groupDn);
            }
        }

        var role = ResolveRole(s.RoleMappings, matched);
        return new LdapLoginResult(
            true,
            role,
            Attr(entry, "sAMAccountName") ?? sam,
            Attr(entry, "displayName"),
            Attr(entry, "mail"),
            role is null ? "The account is not a member of any group mapped to a NetWatch role." : null,
            matched);
    }

    private static string? Attr(SearchResultEntry entry, string name) =>
        entry.Attributes.Contains(name) && entry.Attributes[name].Count > 0 ? entry.Attributes[name][0]?.ToString() : null;

    private static LdapLoginResult Fail(string error) => new(false, null, null, null, null, error, Array.Empty<string>());
}
