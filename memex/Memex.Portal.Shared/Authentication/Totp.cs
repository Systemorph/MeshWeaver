using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// RFC 6238 time-based one-time passwords (HMAC-SHA1, 30-second steps, 6 digits) — the last rung of
/// the approval step-up, used only where no passkey is possible (<c>Doc/Architecture/ApprovalStepUp</c>).
/// Pure: the clock and the secret are arguments. Each accepted step is returned so the caller can
/// refuse it the second time (a code is accepted once).
/// </summary>
public static class Totp
{
    /// <summary>Step length.</summary>
    public const int StepSeconds = 30;

    /// <summary>Code length.</summary>
    public const int Digits = 6;

    /// <summary>How many steps either side of now a code may come from (clock drift).</summary>
    public const int Window = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A fresh 160-bit secret.</summary>
    /// <returns>The secret bytes.</returns>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>The time step of <paramref name="at"/>.</summary>
    /// <param name="at">The instant.</param>
    /// <returns>The step number.</returns>
    public static long StepOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for one step (RFC 4226 dynamic truncation).</summary>
    /// <param name="secret">The shared secret.</param>
    /// <param name="step">The time step.</param>
    /// <param name="digits">The code length.</param>
    /// <returns>The zero-padded code.</returns>
    public static string Code(byte[] secret, long step, int digits = Digits)
    {
        var counter = new byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xff); step >>= 8; }
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulo = (int)Math.Pow(10, digits);
        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// The step a submitted code matches within the drift window and AFTER <paramref name="lastAcceptedStep"/>,
    /// or null. Compared in constant time.
    /// </summary>
    /// <param name="secret">The shared secret.</param>
    /// <param name="code">What the user typed (spaces ignored).</param>
    /// <param name="now">The clock.</param>
    /// <param name="lastAcceptedStep">The last step already accepted for this secret.</param>
    /// <returns>The matched step, or null.</returns>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long lastAcceptedStep)
    {
        var typed = (code ?? "").Replace(" ", "", StringComparison.Ordinal);
        if (typed.Length != Digits || !typed.All(char.IsAsciiDigit)) return null;
        var current = StepOf(now);
        long? matched = null;
        for (var step = current - Window; step <= current + Window; step++)
        {
            if (step <= lastAcceptedStep) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(typed)))
                matched ??= step;
        }
        return matched;
    }

    /// <summary>RFC 4648 base32 (no padding) — how authenticator apps take a secret.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The base32 text.</returns>
    public static string Base32(byte[] bytes)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>The <c>otpauth://</c> URI an authenticator app scans.</summary>
    /// <param name="issuer">The instance name.</param>
    /// <param name="account">The account label.</param>
    /// <param name="secret">The secret.</param>
    /// <returns>The URI.</returns>
    public static string OtpAuthUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
        + $"?secret={Base32(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    /// <summary>Ten fresh one-time recovery codes (<c>xxxxx-xxxxx</c>, lowercase base32).</summary>
    /// <returns>The codes.</returns>
    public static IReadOnlyList<string> NewRecoveryCodes() =>
        Enumerable.Range(0, 10).Select(_ =>
        {
            var raw = Base32(RandomNumberGenerator.GetBytes(7))[..10].ToLowerInvariant();
            return raw[..5] + "-" + raw[5..];
        }).ToList();

    /// <summary>The stored form of a recovery code: SHA-256 hex of its normalized text.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The hash.</returns>
    public static string HashRecoveryCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (code ?? "").Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant()))).ToLowerInvariant();
}
