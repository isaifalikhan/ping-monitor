using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NetWatch.Core.Entities;

namespace NetWatch.Web.Auth;

/// <summary>
/// PBKDF2 password hashing (ASP.NET Core Identity v3 format: HMAC-SHA512, 210,000 iterations as recommended by
/// OWASP for PBKDF2-SHA512) and password policy.
/// </summary>
public sealed class PasswordService
{
    private readonly PasswordHasher<User> _hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = 210_000,
    }));

    // Pre-computed hash used to keep timing identical when the user does not exist.
    private readonly Lazy<string> _dummyHash;

    public PasswordService()
    {
        _dummyHash = new Lazy<string>(() => _hasher.HashPassword(new User(), Guid.NewGuid().ToString()));
    }

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    /// <summary>Verifies a password. Returns (valid, needsRehash).</summary>
    public (bool Valid, bool NeedsRehash) Verify(User? user, string password)
    {
        if (user?.PasswordHash is null)
        {
            _hasher.VerifyHashedPassword(new User(), _dummyHash.Value, password);
            return (false, false);
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return (result != PasswordVerificationResult.Failed, result == PasswordVerificationResult.SuccessRehashNeeded);
    }

    /// <summary>Returns an error message when the password does not meet the policy, otherwise null.</summary>
    public static string? Validate(string? password, string username, int minLength)
    {
        if (string.IsNullOrEmpty(password) || password.Length < minLength)
        {
            return $"Password must be at least {minLength} characters.";
        }

        if (password.Length > 256)
        {
            return "Password is too long.";
        }

        if (password.Contains(username, StringComparison.OrdinalIgnoreCase))
        {
            return "Password must not contain the user name.";
        }

        var classes = 0;
        classes += password.Any(char.IsLower) ? 1 : 0;
        classes += password.Any(char.IsUpper) ? 1 : 0;
        classes += password.Any(char.IsDigit) ? 1 : 0;
        classes += password.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0;
        return classes < 3 ? "Password must contain at least three of: lower case, upper case, digit, symbol." : null;
    }

    /// <summary>Generates a random password that satisfies the policy.</summary>
    public static string Generate(int length = 20)
    {
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string symbols = "!@#$%*-_=+?";
        const string all = lower + upper + digits + symbols;
        var chars = new char[length];
        chars[0] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[1] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var i = 4; i < length; i++)
        {
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        }

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}
