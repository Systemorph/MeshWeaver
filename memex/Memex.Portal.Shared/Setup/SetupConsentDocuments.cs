using System.Security.Cryptography;
using System.Text;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// The privacy statement and platform terms the first-run wizard shows, and the hashes that pin
/// WHICH text a person accepted.
///
/// <para>🚨 <b>The hash is the evidence, not the boolean.</b> A consent record saying "accepted"
/// cannot answer the only question that matters later — accepted WHAT? These hashes travel with the
/// registration and are stored beside it, so a document that changes afterwards is visibly a
/// different document rather than silently the same one.</para>
///
/// <para>The URLs are where the full documents live; the wizard links them rather than reproducing
/// them, because a setup page that inlines a licence is a page nobody reads. The SUMMARY is what it
/// shows inline — short enough to be read, specific enough to be honest about what registering
/// does.</para>
/// </summary>
public static class SetupConsentDocuments
{
    /// <summary>Where the privacy statement lives.</summary>
    public const string PrivacyStatementUrl = "https://memex.meshweaver.cloud/Doc/Legal/Privacy";

    /// <summary>Where the platform terms live.</summary>
    public const string TermsUrl = "https://memex.meshweaver.cloud/Doc/Legal/Terms";

    /// <summary>
    /// The version marker the hashes are taken over.
    ///
    /// <para>Hashing a marker rather than fetching the live documents is deliberate: the wizard runs
    /// before this instance has storage, often before it has general internet access, and a consent
    /// step that cannot complete offline would block setup on a network fetch. Bumping this string
    /// is what makes previously recorded consent visibly older than the current documents.</para>
    /// </summary>
    private const string PrivacyStatementVersion = "privacy/2026-09-08";

    /// <summary>The terms version the hash is taken over. See <see cref="PrivacyStatementVersion"/>.</summary>
    private const string TermsVersion = "terms/2026-09-08";

    /// <summary>Hash of the privacy statement version this build shows.</summary>
    public static string PrivacyStatementHash { get; } = Sha256(PrivacyStatementVersion);

    /// <summary>Hash of the terms version this build shows.</summary>
    public static string TermsHash { get; } = Sha256(TermsVersion);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
