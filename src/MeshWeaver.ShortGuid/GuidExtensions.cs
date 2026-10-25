#nullable enable
using System.Buffers.Text;

namespace MeshWeaver.ShortGuid;

/// <summary>
/// Provides extension methods for converting between <see cref="Guid"/> values and their
/// compact, URL-safe ShortGuid string representations.
/// </summary>
/// <remarks>
/// The encoding is the 16 bytes of <see cref="Guid.ToByteArray()"/> as URL-safe Base64
/// (<c>+</c> → <c>-</c>, <c>/</c> → <c>_</c>) with the two padding characters dropped: always
/// 22 characters. These ids are persisted, so the format is frozen; it is byte-identical to the
/// <c>CSharpVitamins.ShortGuid</c> 2.0.0 package this project used to wrap, and decoding keeps that
/// package's strict behaviour (exactly 22 characters, and the decoded value must re-encode to the
/// very same string).
/// </remarks>
public static class GuidExtensions
{
    private const int EncodedLength = 22;

    /// <summary>
    /// Converts a ShortGuid string back into a <see cref="Guid"/>.
    /// </summary>
    /// <param name="id">The ShortGuid string to decode. A <c>null</c> or empty value yields <see cref="Guid.Empty"/>.</param>
    /// <returns>The decoded <see cref="Guid"/>, or <see cref="Guid.Empty"/> when <paramref name="id"/> is null or empty.</returns>
    /// <exception cref="ArgumentException">When <paramref name="id"/> is not exactly 22 characters long
    /// (a 36-character <see cref="Guid"/> string is NOT accepted), or decodes to fewer than 16 bytes.</exception>
    /// <exception cref="FormatException">When <paramref name="id"/> is not valid Base64, or does not
    /// re-encode to exactly the same string (the strict round-trip check).</exception>
    public static Guid AsGuid(this string id)
    {
        if (string.IsNullOrEmpty(id))
            return Guid.Empty;
        return Decode(id);
    }

    /// <summary>
    /// Converts a <see cref="Guid"/> into its compact, URL-safe ShortGuid string representation.
    /// </summary>
    /// <param name="guid">The <see cref="Guid"/> to encode. <see cref="Guid.Empty"/> yields an empty string.</param>
    /// <returns>The ShortGuid string, or an empty string when <paramref name="guid"/> is <see cref="Guid.Empty"/>.</returns>
    public static string AsString(this Guid guid)
    {
        if (guid == Guid.Empty)
            return string.Empty;

        return Encode(guid);
    }

    private static string Encode(Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    // Mirrors the decode the package performed, step for step, so that every input — valid or
    // not — yields the same Guid or the same exception type as before: the length check, the
    // standard-alphabet decode (which tolerates whitespace and so can produce fewer than 16
    // bytes, rejected by the Guid constructor), then the strict round-trip comparison.
    private static Guid Decode(string id)
    {
        if (id.Length != EncodedLength)
            throw new ArgumentException(
                $"A ShortGuid must be exactly {EncodedLength} characters long. Received a {id.Length} character string.",
                nameof(id));

        var bytes = Convert.FromBase64String(id.Replace('_', '/').Replace('-', '+') + "==");
        var guid = new Guid(bytes);
        var roundTrip = Encode(guid);
        if (!string.Equals(roundTrip, id, StringComparison.Ordinal))
            throw new FormatException(
                $"Invalid strict ShortGuid encoded string. The string '{id}' is valid URL-safe Base64, but failed a round-trip test expecting '{roundTrip}'.");
        return guid;
    }
}
