using System.Text.Json;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// A non-2xx answer from Microsoft's token endpoint, read for the ONE thing that matters to the
/// Executive Assistant: did Entra say the stored grant is dead, or did it say nothing about it?
///
/// <para>The OAuth 2.0 error shape (RFC 6749 §5.2, and Entra's extension of it) is a 400 with a
/// JSON body <c>{ "error": "…", "error_description": "AADSTS…: …" }</c>. Three <c>error</c> values
/// name the GRANT: <c>invalid_grant</c> (revoked, expired, password changed, consent withdrawn, or a
/// refresh token minted for a narrower scope set than the one requested), <c>interaction_required</c>
/// and <c>consent_required</c> (the user must go through the dialog again). Those are
/// <see cref="GrantRefused"/>: the remedy is consent, and saying "retry in a moment" to them is the
/// defect in MeshWeaver.Plugins#1615 — the user retries forever. Everything else — 401
/// <c>invalid_client</c> (OUR secret), 429, 5xx, a body that is not that shape — says nothing about
/// the grant and stays undetermined (#3433).</para>
///
/// <para>Pure: a status and a body in, a verdict out, so it is pinned without a network
/// (<c>EaCredentialReadTest</c>).</para>
/// </summary>
public sealed record TokenRefusal(int StatusCode, string? Error, string? Description)
{
    /// <summary>The OAuth <c>error</c> values that name the grant itself rather than us or the service.</summary>
    private static readonly string[] GrantErrors = ["invalid_grant", "interaction_required", "consent_required"];

    /// <summary>True when Entra answered that the stored grant cannot be redeemed — consent is the remedy.</summary>
    public bool GrantRefused =>
        StatusCode == 400 && Error is not null
        && Array.Exists(GrantErrors, e => string.Equals(e, Error, StringComparison.Ordinal));

    /// <summary>The endpoint's own words, for the diagnostic and the credential's stamp.</summary>
    public string Summary =>
        Error is null
            ? $"HTTP {StatusCode}"
            : Description is null ? Error : $"{Error}: {FirstLine(Description)}";

    /// <summary>Reads one token-endpoint answer. Never throws: a body that is not the OAuth error shape is a refusal with no <c>error</c>.</summary>
    public static TokenRefusal Of(int statusCode, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new TokenRefusal(statusCode, null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new TokenRefusal(statusCode, null, null);
            var error = doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
            var description = doc.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
            return new TokenRefusal(statusCode, error, description);
        }
        catch (JsonException)
        {
            return new TokenRefusal(statusCode, null, null);
        }
    }

    // Entra's error_description is several lines (the AADSTS sentence, a trace id, a correlation id,
    // a timestamp); the first line is the sentence a person can act on.
    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text.Trim() : text[..end].Trim();
    }
}
