using System.Net.Http.Json;
using Azure.Core;

namespace Memex.Portal.Shared.Authentication;

/// <summary>Writes one collected value into an instance's Key Vault.</summary>
public interface ISetupSecretWriter
{
    /// <summary>
    /// Writes <paramref name="value"/> as <paramref name="objectName"/> in <paramref name="vault"/>.
    /// </summary>
    /// <param name="vault">The vault name, as the deployment record states it.</param>
    /// <param name="objectName">The object name, derived by the fleet's rule.</param>
    /// <param name="value">The secret. Never logged, never returned, never stored anywhere else.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Null on success; the reason on failure — never containing the value.</returns>
    Task<string?> WriteAsync(string vault, string objectName, string value, CancellationToken cancellationToken);
}

/// <summary>
/// The Key Vault writer, over the vault's REST API with a token from this instance's own managed
/// identity.
///
/// <para><b>REST rather than the SDK</b> because <c>Azure.Identity</c> is already a dependency here
/// and <c>Azure.Security.KeyVault.Secrets</c> is not: one PUT is not worth a package, an audit
/// entry and a version to keep.</para>
///
/// <para>🚨 <b>This needs a write grant the control instance does not have — and the obvious way to
/// give it is wrong.</b> Measured 2026-09-16 on <c>memexaks-portal-mi</c> (subscription 7ecc5974,
/// resource group memex-aks-rg): that ONE managed identity carries five federated credentials —
/// <c>system:serviceaccount:{atioz,memex,memex-cloud,build,pearl}:memex-portal-sa</c>. It is not the
/// control instance's identity; it is every portal's identity on that cluster. Granting it
/// <i>Secrets Officer</i> on the <c>Systemorph</c> vault would give WRITE over every object in that
/// vault to every instance on the cluster, a CLIENT instance included — the exact opposite of why
/// the hand-off goes through the control instance at all.</para>
///
/// <para><b>What to grant instead.</b> A DEDICATED identity for the control instance, federated only
/// to <c>system:serviceaccount:memex:memex-portal-sa</c>, holding Secrets Officer, while the shared
/// identity keeps read. That is the smaller change and the recommended one. Scoping the grant per
/// SECRET OBJECT instead is supported by Key Vault RBAC, but it does not scale past a handful of
/// names and still lands on the shared identity.</para>
///
/// <para>Until one of those exists, every call here refuses with the vault's own 403 — correct
/// behaviour, reported as a refusal the maintainer can act on rather than as a mysterious failure.
/// The alternative designs are worse: handing the values to the operator lane would put them in
/// workflow inputs, and writing them from the instance would give every client portal write access
/// to its own secrets.</para>
/// </summary>
/// <param name="httpClient">The client used for the PUT.</param>
/// <param name="credential">
/// The token source, supplied by the host — normally the instance's own managed identity. Required
/// rather than defaulted: this assembly is also built for the browser, where a managed-identity
/// credential does not exist, and a type that constructs one unconditionally cannot compile there.
/// The registration is where the server's credential belongs anyway.
/// </param>
public sealed class KeyVaultSetupSecretWriter(HttpClient httpClient, TokenCredential credential)
    : ISetupSecretWriter
{

    /// <summary>The scope a vault token is requested for.</summary>
    public const string Scope = "https://vault.azure.net/.default";

    /// <inheritdoc />
    public async Task<string?> WriteAsync(
        string vault, string objectName, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vault);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        try
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext([Scope]), cancellationToken);

            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"https://{vault}.vault.azure.net/secrets/{Uri.EscapeDataString(objectName)}?api-version=7.4")
            {
                Content = JsonContent.Create(new { value }),
            };
            request.Headers.Authorization = new("Bearer", token.Token);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return null;

            // 🚨 The vault's body is echoed because it names the missing role — and it cannot
            // contain the value, which travelled in the REQUEST. A refusal nobody can read is a
            // refusal nobody can fix.
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            return $"the vault refused '{objectName}' with {(int)response.StatusCode}: "
                   + Truncate(detail, 300);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message, never the exception's full detail: a stack trace from a JSON serializer
            // can carry the payload it was serializing.
            return $"the vault could not be reached for '{objectName}': {ex.GetType().Name}";
        }
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";
}
