using System.Text.RegularExpressions;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The two text transforms every outbound telemetry field goes through
/// (<c>Doc/Architecture/InstanceTelemetry</c>):
/// <list type="bullet">
/// <item><see cref="Mask"/> removes anything that could be a secret or a person — bearer and basic
/// credentials, JWTs, known token prefixes, query-string and key=value secrets, URL user info,
/// e-mail addresses. Applied by the SENDER before a digest leaves the process and AGAIN by the
/// registry on receipt, so neither side has to trust the other to have done it.</item>
/// <item><see cref="Normalize"/> turns a FORMATTED message into a template-shaped string — GUIDs,
/// hex runs, quoted literals, paths and numbers become placeholders — for the one case where a log
/// call carried no <c>{OriginalFormat}</c> and the template had to be reconstructed. Values are the
/// part of a log line that varies and the part that can leak; a template has none.</item>
/// </list>
/// Deliberately conservative: a false positive masks a harmless word, a false negative ships a
/// secret, so every pattern errs towards masking.
/// </summary>
public static partial class TelemetryRedaction
{
    /// <summary>What a masked span is replaced with.</summary>
    public const string Masked = "<masked>";

    /// <summary>What an e-mail address is replaced with.</summary>
    public const string MaskedEmail = "<email>";

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Credential();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(mwi_|mwa_|mwr_|ghp_|gho_|ghs_|ghu_|github_pat_|sk-|xox[abp]-)[A-Za-z0-9_\-]{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"(?i)([?&;](?:sig|signature|token|key|secret|password|pwd|access_token|refresh_token|client_secret|sas|code|api[_-]?key)=)[^&\s""']+", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecret();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|accountkey|sharedaccesskey|sharedaccesssignature|authorization|connectionstring)\s*[=:]\s*[^;\s,""']+", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"(?i)(https?://)[^/\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Guid();

    [GeneratedRegex(@"\b[0-9a-fA-F]{16,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexRun();

    [GeneratedRegex(@"""[^""\r\n]*""|'[^'\r\n]*'", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"(?<![\w{<])(?:/[\w.\-]+){2,}/?", RegexOptions.CultureInvariant)]
    private static partial Regex PathLike();

    [GeneratedRegex(@"(?<![\w{<.-])\d+(?:[.,]\d+)*(?![\w}>])", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>Masks credentials and personal identifiers. Idempotent; never throws; null-safe.</summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var masked = text;
        masked = UrlUserInfo().Replace(masked, "$1" + Masked + "@");
        masked = Jwt().Replace(masked, Masked);
        masked = Credential().Replace(masked, "$1 " + Masked);
        masked = KnownToken().Replace(masked, Masked);
        masked = QuerySecret().Replace(masked, "$1" + Masked);
        masked = KeyValueSecret().Replace(masked, "$1=" + Masked);
        masked = Email().Replace(masked, MaskedEmail);
        return masked;
    }

    /// <summary>
    /// <see cref="Mask"/>, then every value-shaped span becomes a placeholder: <c>{guid}</c>,
    /// <c>{hex}</c>, <c>{value}</c> (a quoted literal), <c>{path}</c>, <c>{n}</c>; whitespace
    /// collapsed. What comes out groups like a template and carries no value.
    /// </summary>
    public static string Normalize(string? text)
    {
        var normalized = Mask(text);
        if (normalized.Length == 0)
            return "";
        normalized = Guid().Replace(normalized, "{guid}");
        normalized = HexRun().Replace(normalized, "{hex}");
        normalized = Quoted().Replace(normalized, "{value}");
        normalized = PathLike().Replace(normalized, "{path}");
        normalized = Number().Replace(normalized, "{n}");
        return Whitespace().Replace(normalized, " ").Trim();
    }
}
