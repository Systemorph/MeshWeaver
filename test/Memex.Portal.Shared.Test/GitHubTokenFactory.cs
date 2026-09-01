using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshWeaver.Mesh.Security;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Mints GitHub-Actions-shaped OIDC tokens and the JWKS that verifies them, so the build-principal
/// leg (#2483) can be exercised against REAL RSA signatures rather than a stubbed verifier.
///
/// <para>🚨 This is the producer half of the contract under test, written independently of
/// <see cref="GitHubActionsToken"/>: it signs with <see cref="RSA"/> directly and lays the JWKS out
/// the way GitHub does. A helper that reused the verifier's own encoder could agree with a broken
/// one — the classic gate-that-tests-its-own-input.</para>
/// </summary>
internal sealed class GitHubTokenFactory : IDisposable
{
    private readonly RSA key = RSA.Create(2048);

    /// <summary>The <c>kid</c> this factory's key is published under.</summary>
    public string KeyId { get; init; } = "test-key-1";

    /// <summary>The JWKS document publishing this factory's public key, exactly as GitHub lays one
    /// out — plus, deliberately, entries this verifier must skip.</summary>
    /// <param name="extras">Extra raw JWK JSON objects to include beside the real key.</param>
    /// <returns>The JWKS document.</returns>
    public string Jwks(params string[] extras)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var entries = new List<string>
        {
            $$"""
            {"kty":"RSA","use":"sig","alg":"RS256","kid":"{{KeyId}}",
             "n":"{{Base64Url(parameters.Modulus!)}}","e":"{{Base64Url(parameters.Exponent!)}}"}
            """,
        };
        entries.AddRange(extras);
        return $$"""{"keys":[{{string.Join(",", entries)}}]}""";
    }

    /// <summary>A JWKS that publishes a DIFFERENT key under the same <c>kid</c> — the shape a
    /// signature forged with a key of the attacker's own choosing has to fail against.</summary>
    /// <returns>The JWKS document.</returns>
    public string JwksOfAStranger()
    {
        using var stranger = RSA.Create(2048);
        var parameters = stranger.ExportParameters(includePrivateParameters: false);
        return $$"""
            {"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"{{KeyId}}",
             "n":"{{Base64Url(parameters.Modulus!)}}","e":"{{Base64Url(parameters.Exponent!)}}"}]}
            """;
    }

    /// <summary>The parsed key set this factory's tokens verify against.</summary>
    /// <returns>Keys by <c>kid</c>.</returns>
    public IReadOnlyDictionary<string, GitHubSigningKey> Keys() => GitHubActionsToken.ParseJwks(Jwks());

    /// <summary>
    /// Mints a token. Every claim has a realistic default so a test names only what it is varying.
    /// </summary>
    /// <param name="audience">The <c>aud</c> claim.</param>
    /// <param name="repository">The <c>repository</c> claim.</param>
    /// <param name="eventName">The <c>event_name</c> claim.</param>
    /// <param name="gitRef">The <c>ref</c> claim.</param>
    /// <param name="issuer">The <c>iss</c> claim.</param>
    /// <param name="issuedAt">Issue instant; <c>nbf</c>/<c>iat</c> come from here.</param>
    /// <param name="lifetime">How long after <paramref name="issuedAt"/> the token expires.</param>
    /// <param name="algorithm">The <c>alg</c> header — vary it to forge.</param>
    /// <param name="keyId">The <c>kid</c> header — vary it to name a key nobody publishes.</param>
    /// <param name="repositoryId">The <c>repository_id</c> claim.</param>
    /// <param name="signWith">Sign with this key instead of the factory's own.</param>
    /// <param name="audienceJson">Raw JSON for <c>aud</c> (e.g. an array), overriding
    /// <paramref name="audience"/>.</param>
    /// <returns>The token string.</returns>
    public string Mint(
        string audience,
        string repository = "Systemorph/MeshWeaver.SocialMedia",
        string eventName = "push",
        string gitRef = "refs/heads/main",
        string issuer = GitHubActionsToken.Issuer,
        DateTimeOffset? issuedAt = null,
        TimeSpan? lifetime = null,
        string algorithm = "RS256",
        string? keyId = null,
        string repositoryId = "123456789",
        RSA? signWith = null,
        string? audienceJson = null)
    {
        var start = issuedAt ?? DateTimeOffset.UtcNow;
        var expires = start.Add(lifetime ?? TimeSpan.FromMinutes(10));
        var header = $$"""{"alg":"{{algorithm}}","typ":"JWT","kid":"{{keyId ?? KeyId}}","x5t":"ignored"}""";
        var payload = $$"""
            {"iss":{{Json(issuer)}},"sub":"repo:{{repository}}:ref:{{gitRef}}",
             "aud":{{audienceJson ?? Json(audience)}},
             "iat":{{start.ToUnixTimeSeconds()}},"nbf":{{start.ToUnixTimeSeconds()}},
             "exp":{{expires.ToUnixTimeSeconds()}},"jti":"{{Guid.NewGuid():N}}",
             "repository":{{Json(repository)}},"repository_id":{{Json(repositoryId)}},
             "repository_owner":{{Json(repository.Split('/')[0])}},"repository_owner_id":"9999",
             "event_name":{{Json(eventName)}},"ref":{{Json(gitRef)}},
             "workflow":"CI","actor":"rbuergi","run_id":"42",
             "job_workflow_ref":"{{repository}}/.github/workflows/ci.yml@{{gitRef}}"}
            """;

        var signingInput = Base64Url(Encoding.UTF8.GetBytes(Compact(header)))
                           + "." + Base64Url(Encoding.UTF8.GetBytes(Compact(payload)));
        var signature = (signWith ?? key).SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>A token whose PAYLOAD was swapped after signing — a valid signature over different
    /// bytes, which is the only interesting forgery once `alg` is pinned.</summary>
    /// <param name="audience">The <c>aud</c> of the honest token.</param>
    /// <param name="honestRepository">The repository actually signed for.</param>
    /// <param name="swappedRepository">The repository substituted afterwards.</param>
    /// <returns>The tampered token.</returns>
    public string MintWithSwappedPayload(
        string audience, string honestRepository, string swappedRepository)
    {
        var honest = Mint(audience, honestRepository);
        var forged = Mint(audience, swappedRepository);
        var honestParts = honest.Split('.');
        var forgedParts = forged.Split('.');
        // header + payload from the forgery, signature from the honest token.
        return $"{forgedParts[0]}.{forgedParts[1]}.{honestParts[2]}";
    }

    /// <inheritdoc />
    public void Dispose() => key.Dispose();

    private static string Compact(string json) =>
        string.Concat(json.Where(c => c is not ('\n' or '\r' or ' ')));

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
