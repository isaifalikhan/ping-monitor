using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetWatch.Core.Credentials;

/// <summary>
/// Secret part of a credential profile. Serialised to JSON and encrypted with DPAPI before storage.
/// Only the fields relevant to the profile kind are used.
/// </summary>
public sealed class CredentialSecret
{
    /// <summary>SNMP v1/v2c community string.</summary>
    public string? Community { get; set; }

    /// <summary>SNMPv3 user, Windows/SSH/vCenter user name.</summary>
    public string? Username { get; set; }

    /// <summary>Windows/SSH/vCenter password.</summary>
    public string? Password { get; set; }

    /// <summary>Windows domain (optional, can also be given as DOMAIN\user).</summary>
    public string? Domain { get; set; }

    public SnmpAuthProtocol AuthProtocol { get; set; } = SnmpAuthProtocol.None;
    public string? AuthPassword { get; set; }
    public SnmpPrivProtocol PrivProtocol { get; set; } = SnmpPrivProtocol.None;
    public string? PrivPassword { get; set; }

    /// <summary>SNMPv3 context name (rarely needed).</summary>
    public string? ContextName { get; set; }

    /// <summary>SSH private key (PEM/OpenSSH).</summary>
    public string? PrivateKey { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static CredentialSecret FromJson(string json) =>
        JsonSerializer.Deserialize<CredentialSecret>(json, JsonOptions) ?? new CredentialSecret();

    /// <summary>Validates the fields required for a given kind. Returns an error message or null.</summary>
    public string? Validate(CredentialKind kind)
    {
        switch (kind)
        {
            case CredentialKind.SnmpV2c:
                return string.IsNullOrEmpty(Community) ? "Community string is required." : null;
            case CredentialKind.SnmpV3:
                if (string.IsNullOrWhiteSpace(Username))
                {
                    return "SNMPv3 user name is required.";
                }

                if (AuthProtocol == SnmpAuthProtocol.None && PrivProtocol != SnmpPrivProtocol.None)
                {
                    return "Privacy (encryption) requires an authentication protocol.";
                }

                if (AuthProtocol != SnmpAuthProtocol.None && (AuthPassword?.Length ?? 0) < 8)
                {
                    return "SNMPv3 authentication password must be at least 8 characters.";
                }

                if (PrivProtocol != SnmpPrivProtocol.None && (PrivPassword?.Length ?? 0) < 8)
                {
                    return "SNMPv3 privacy password must be at least 8 characters.";
                }

                return null;
            case CredentialKind.Windows:
            case CredentialKind.VCenter:
                return string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password)
                    ? "User name and password are required."
                    : null;
            case CredentialKind.Ssh:
                return string.IsNullOrWhiteSpace(Username) || (string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(PrivateKey))
                    ? "User name and a password or private key are required."
                    : null;
            default:
                return "Unknown credential kind.";
        }
    }

    /// <summary>Non-secret summary shown in the UI after saving.</summary>
    public string BuildDisplayHint(CredentialKind kind) => kind switch
    {
        CredentialKind.SnmpV2c => "community: ********",
        CredentialKind.SnmpV3 => $"user: {Username}, auth: {AuthProtocol}, priv: {PrivProtocol}",
        _ => string.IsNullOrEmpty(Domain) ? $"user: {Username}" : $"user: {Domain}\\{Username}",
    };
}
