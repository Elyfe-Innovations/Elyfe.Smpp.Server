using System.Security.Cryptography;
using System.Text;

namespace Elyfe.Smpp.Server.Authentication;

/// <summary>
///     Shared credential verification logic used by the static authenticators. Supports SHA-256 hashed passwords and,
///     for development convenience, plain-text passwords.
/// </summary>
internal static class SmppAccountVerifier
{
    /// <summary>
    ///     Verifies the supplied credentials against a configured account and returns the matching authentication
    ///     result.
    /// </summary>
    public static AuthenticationResult Verify(SmppAccount? account, SmppCredentials credentials)
    {
        if (account is null)
        {
            return AuthenticationResult.Fail("Unknown system_id.");
        }

        if (!account.Enabled)
        {
            return AuthenticationResult.Fail("Account disabled.");
        }

        if (string.IsNullOrEmpty(account.TenantId))
        {
            return AuthenticationResult.Fail("Account has no tenant mapping.");
        }

        if (!PasswordMatches(account, credentials.Password))
        {
            return AuthenticationResult.Fail("Invalid password.");
        }

        return AuthenticationResult.Success(account.TenantId);
    }

    private static bool PasswordMatches(SmppAccount account, string presented)
    {
        if (!string.IsNullOrEmpty(account.PasswordSha256))
        {
            var hash = ComputeSha256Hex(presented);
            return FixedTimeEquals(hash, account.PasswordSha256.Trim().ToLowerInvariant());
        }

        if (account.Password is not null)
        {
            return FixedTimeEquals(presented, account.Password);
        }

        return false;
    }

    /// <summary>Computes the lower-case hex SHA-256 hash of the supplied value.</summary>
    public static string ComputeSha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return aBytes.Length == bBytes.Length && CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
