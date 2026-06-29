#nullable enable
namespace MeshWeaver.ShortGuid;

/// <summary>
/// Provides extension methods for converting between <see cref="Guid"/> values and their
/// compact, URL-safe ShortGuid string representations.
/// </summary>
public static class GuidExtensions
{
    /// <summary>
    /// Converts a ShortGuid string back into a <see cref="Guid"/>.
    /// </summary>
    /// <param name="id">The ShortGuid string to decode. A <c>null</c> or empty value yields <see cref="Guid.Empty"/>.</param>
    /// <returns>The decoded <see cref="Guid"/>, or <see cref="Guid.Empty"/> when <paramref name="id"/> is null or empty.</returns>
    public static Guid AsGuid(this string id)
    {
        if (string.IsNullOrEmpty(id))
            return Guid.Empty;
        return new CSharpVitamins.ShortGuid(id);
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

        return new CSharpVitamins.ShortGuid(guid).ToString();
    }
}
