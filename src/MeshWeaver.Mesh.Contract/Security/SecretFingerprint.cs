using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The short, public identifier of a secret VALUE. It shows that two places hold the same value
/// without showing the value. For example, the control instance's copy of a deployment's
/// announcement key and the copy the deployment signs with have the same fingerprint exactly when
/// they are the same key.
///
/// <para>🚨 <b>One rule, fleet-wide, byte for byte.</b> The operator's vault writer computes the same
/// fingerprint and stores it as the vault tag <c>mw-fp</c>, so a fingerprint shown by the portal and
/// one read from a vault tag must agree. The rule is:</para>
/// <list type="bullet">
/// <item>The ALPHABET is 26 if the value has any <c>a–z</c>, plus 26 if any <c>A–Z</c>, plus 10 if
/// any <c>0–9</c>, plus 32 if any other byte.</item>
/// <item>The estimated strength is <c>floor(byteLength × log2(alphabet))</c> bits.</item>
/// <item>At 128 bits or more, the fingerprint is <c>sha256:</c> followed by the first 12 lowercase
/// hex digits of SHA-256 over the value's UTF-8 bytes. Below that, or with an empty alphabet, it is
/// <see cref="Withheld"/>.</item>
/// </list>
///
/// <para>Why withhold: an unkeyed hash of a LOW-entropy value (a password, a short PIN) is a
/// dictionary-attack oracle, because anyone who sees it can test guesses against it. A generated
/// key (<see cref="Generate"/>, 256 bits) always gets a real fingerprint. No keyed HMAC is used, so
/// any holder of the value can recompute the fingerprint independently.</para>
/// </summary>
public static class SecretFingerprint
{
    /// <summary>The prefix every published fingerprint carries, naming the algorithm.</summary>
    public const string Prefix = "sha256:";

    /// <summary>What is shown instead of a fingerprint for a value too weak to hash in public.</summary>
    public const string Withheld = "withheld:low-entropy";

    /// <summary>How many hex digits of the digest are shown (48 bits).</summary>
    public const int HexDigits = 12;

    /// <summary>The estimated strength a value needs before its hash is shown.</summary>
    public const int MinimumBits = 128;

    /// <summary>The number of random bytes <see cref="Generate"/> draws (256 bits).</summary>
    public const int GeneratedBytes = 32;

    /// <summary>
    /// The fingerprint of <paramref name="value"/>: <c>sha256:</c> plus 12 hex digits, or
    /// <see cref="Withheld"/> for a low-entropy value, or null for a null or empty value. The value is
    /// used byte for byte, without trimming, so the fingerprint matches what is actually used.
    /// </summary>
    /// <param name="value">The secret value.</param>
    public static string? Of(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        var bytes = Encoding.UTF8.GetBytes(value);
        if (EstimatedBits(bytes) < MinimumBits)
            return Withheld;
        var digest = SHA256.HashData(bytes);
        return Prefix + Convert.ToHexString(digest)[..HexDigits].ToLowerInvariant();
    }

    /// <summary>
    /// The estimated strength of <paramref name="bytes"/> in bits, by the alphabet rule above.
    /// 0 for an empty value.
    /// </summary>
    /// <param name="bytes">The value's UTF-8 bytes.</param>
    public static int EstimatedBits(ReadOnlySpan<byte> bytes)
    {
        bool lower = false, upper = false, digit = false, other = false;
        foreach (var b in bytes)
        {
            if (b is >= (byte)'a' and <= (byte)'z') lower = true;
            else if (b is >= (byte)'A' and <= (byte)'Z') upper = true;
            else if (b is >= (byte)'0' and <= (byte)'9') digit = true;
            else other = true;
        }
        var alphabet = (lower ? 26 : 0) + (upper ? 26 : 0) + (digit ? 10 : 0) + (other ? 32 : 0);
        if (alphabet == 0)
            return 0;
        return (int)Math.Floor(bytes.Length * Math.Log2(alphabet));
    }

    /// <summary>
    /// A new random key: <see cref="GeneratedBytes"/> bytes from the operating system's
    /// cryptographic generator, as lowercase hex. It has the same shape as <c>openssl rand -hex 32</c>.
    /// </summary>
    public static string Generate() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(GeneratedBytes)).ToLowerInvariant();
}
