using System.Security.Cryptography;
using System.Text;
using NetWatch.Core.Abstractions;

namespace NetWatch.Data.Security;

/// <summary>
/// Encrypts secrets with Windows DPAPI in LocalMachine scope plus an application-specific entropy.
/// Data can only be decrypted on the machine that encrypted it, which also means a database backup
/// restored on another server needs credentials to be re-entered (documented in the admin guide).
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Fixed entropy: not a secret, but prevents other DPAPI consumers from trivially decrypting our blobs.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetWatch.CredentialProfiles.v1");

    public byte[] Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        var bytes = ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.LocalMachine);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
