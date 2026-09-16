using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>One value on its way to a vault.</summary>
/// <param name="ConfigKey">The configuration key it answers.</param>
/// <param name="Value">The secret. Never logged, never echoed, never stored outside the vault.</param>
/// <param name="VaultObject">The object it becomes, derived by the sender from the record's prefix.</param>
public sealed record SetupHandOffSecret(string ConfigKey, string Value, string VaultObject)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"SetupHandOffSecret {{ ConfigKey = {ConfigKey}, VaultObject = {VaultObject}, Value = <redacted> }}";
}

/// <summary>What a provisioned instance asks the control instance to do at the end of its setup.</summary>
/// <param name="Deployment">The deployment id, e.g. <c>pearl</c>.</param>
/// <param name="Vault">The vault its record names.</param>
/// <param name="Secrets">The values that become vault objects.</param>
/// <param name="RecordValues">The plain values, which belong on the record.</param>
/// <param name="AdministratorEmail">Who is to administer the instance.</param>
/// <param name="InviteAdministrator">Whether that person must also be invited.</param>
/// <param name="Plugins">What the wizard selected.</param>
public sealed record SetupHandOffBody(
    string Deployment,
    string Vault,
    IReadOnlyList<SetupHandOffSecret> Secrets,
    IReadOnlyDictionary<string, string> RecordValues,
    string AdministratorEmail,
    bool InviteAdministrator,
    IReadOnlyList<string> Plugins);

/// <summary>
/// The control instance's setup hand-off: the values a freshly provisioned instance collected,
/// written into the vault ITS record names, by the one identity entitled to write there.
///
/// <para>🚨 <b>Its own path, deliberately NOT the control inbox.</b> The inbox persists every event
/// it receives — that is what makes it an inbox, and what lets a lost callback be re-read. A body
/// carrying a client's sign-in secret and model keys must not be persisted anywhere, so bending the
/// inbox to make an exception for one event type would put the exception one refactor away from
/// being forgotten. This endpoint keeps the inbox's SIGNATURE scheme and none of its storage.</para>
///
/// <para><b>What is durable when this returns:</b> the vault objects, and nothing else. No node, no
/// activity log, no inbox row, no request log, no response echo. The values exist in this process's
/// memory for the length of the call. <see cref="SetupHandOffSecret.ToString"/> is overridden so an
/// interpolated log line cannot leak one, and every log statement here names the KEY and never the
/// value.</para>
///
/// <para>🚨 <b>What it does not yet do.</b> The plain half — the record values, the selected plugins
/// and the administrator grant — is reported back to the caller and is NOT applied here: those are
/// writes to a deployment record and to another instance's mesh, which belong to the operator path
/// the fleet already steers from. Doing half the job loudly is better than doing it quietly in a
/// place nobody audits.</para>
/// </summary>
[ApiController]
[Route("api/hosting/setup-handoff")]
public class SetupHandOffController(
    IConfiguration configuration,
    ISetupSecretWriter writer,
    ILogger<SetupHandOffController> logger) : ControllerBase
{
    /// <summary>The configuration key holding the shared secret — the inbox's own.</summary>
    public const string SecretKey = "Hosting:PlatformWebhookSecret";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Receives one instance's collected setup values and writes the secret half to its vault.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        // The RAW bytes: the signature is over exactly what arrived, so re-serialising a parsed
        // object before verifying would make a property-order difference look like an attack.
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        var body = buffer.ToArray();

        var refusal = SetupHandOffVerifier.Verify(
            configuration[SecretKey],
            body,
            Request.Headers[SetupHandOffVerifier.SignatureHeader],
            Request.Headers[SetupHandOffVerifier.TimestampHeader],
            Request.Headers[SetupHandOffVerifier.NonceHeader],
            DateTimeOffset.UtcNow);

        if (refusal is not HandOffRefusal.None)
            return Refuse(refusal);

        SetupHandOffBody? request;
        try
        {
            request = JsonSerializer.Deserialize<SetupHandOffBody>(body, Json);
        }
        catch (JsonException)
        {
            // Never the exception's message: a JSON error quotes the document it failed on.
            return BadRequest("the body is not a setup hand-off.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Deployment))
            return BadRequest("the body names no deployment.");
        if (string.IsNullOrWhiteSpace(request.Vault))
            return Refuse(HandOffRefusal.UnknownInstance);

        var written = new List<string>();
        var failures = new List<string>();
        foreach (var secret in request.Secrets ?? [])
        {
            if (string.IsNullOrWhiteSpace(secret.VaultObject) || string.IsNullOrEmpty(secret.Value))
            {
                // The sender derives object names; one that arrives empty means the record had no
                // prefix to derive from. Writing it under a guessed name would put a secret where
                // the instance will never read it.
                failures.Add($"'{secret.ConfigKey}' carries no vault object name — the instance's record has no keyVaultSecretPrefix.");
                continue;
            }

            var failure = await writer.WriteAsync(request.Vault, secret.VaultObject, secret.Value, cancellationToken);
            if (failure is null)
                written.Add(secret.VaultObject);
            else
                failures.Add(failure);
        }

        // KEYS and object names only. This is the log line that would otherwise leak everything.
        logger.LogInformation(
            "Setup hand-off for {Deployment}: wrote {Written} object(s) to vault {Vault}, {Failed} refused. "
            + "Administrator {Administrator}, invite={Invite}, {Plugins} plugin(s) selected.",
            request.Deployment, written.Count, request.Vault, failures.Count,
            request.AdministratorEmail, request.InviteAdministrator, request.Plugins?.Count ?? 0);

        if (failures.Count > 0)
            return StatusCode(502, new
            {
                written,
                failures,
                message = "some values could not be written; nothing was stored anywhere else.",
            });

        return Ok(new
        {
            written,
            // The plain half is reported, not applied — see the type's remarks.
            pending = new
            {
                recordValues = request.RecordValues?.Keys ?? (IEnumerable<string>)[],
                plugins = request.Plugins ?? [],
                administrator = request.AdministratorEmail,
                invite = request.InviteAdministrator,
            },
        });
    }

    /// <summary>
    /// The answer for a refused hand-off. A refusal names WHICH check failed, except that an
    /// unconfigured endpoint answers 404 — it must be indistinguishable from "no such route" so a
    /// control instance that does not offer this does not advertise it.
    /// </summary>
    /// <param name="refusal">Why it was refused.</param>
    private IActionResult Refuse(HandOffRefusal refusal)
    {
        logger.LogWarning("Setup hand-off refused: {Refusal}", refusal);
        return refusal switch
        {
            HandOffRefusal.NotConfigured => NotFound(),
            HandOffRefusal.UnknownInstance => NotFound("no deployment record names that instance."),
            HandOffRefusal.Replayed => Conflict("this hand-off has already been received."),
            HandOffRefusal.Stale => BadRequest("the request is outside the accepted time window."),
            _ => Unauthorized("the request is not signed by this fleet."),
        };
    }
}
