using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// One-shot, secret-gated first-admin bootstrap. Exists because a fresh deployment with a
/// wedged onboarding path (or any environment where the interactive /onboarding flow can't be
/// driven) still needs a way to materialise the FIRST platform administrator.
///
/// <para>It reuses <see cref="UserOnboardingService"/> — the exact same write path the
/// interactive onboarding uses — so the produced User node + AccessAssignments are serialised
/// and schema-routed correctly (no hand-rolled SQL, no guessing the content JSON shape).
/// CreateUser writes to the user's own partition + the <c>Admin/_Access</c> scope; it never
/// touches the phantom <c>onboarding</c> hub, so it isn't affected by that deadlock.</para>
///
/// <para>Gated by the <c>Bootstrap:Secret</c> config value: if unset, the endpoint is disabled
/// (returns 404-equivalent Unauthorized). Intended to be invoked once by an operator, then the
/// secret removed. Anonymous-reachable by design (there is no admin yet to authorise it).</para>
/// </summary>
[ApiController]
[Route("bootstrap")]
public class BootstrapController(
    UserOnboardingService onboarding,
    IConfiguration config,
    ILogger<BootstrapController> logger) : ControllerBase
{
    [HttpGet("first-admin")]
    [HttpPost("first-admin")]
    public async Task<IActionResult> FirstAdmin(
        [FromQuery] string? secret,
        [FromQuery] string? email,
        [FromQuery] string? name,
        [FromQuery] string? username)
    {
        var expected = config["Bootstrap:Secret"];
        if (string.IsNullOrWhiteSpace(expected))
            return NotFound();                       // disabled unless a secret is configured
        if (!string.Equals(secret, expected, StringComparison.Ordinal))
            return Unauthorized("invalid or missing secret");
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest("email query parameter is required");

        var user = (string.IsNullOrWhiteSpace(username) ? email.Split('@')[0] : username)
            .Trim().ToLowerInvariant();
        var request = new UserOnboardingRequest(user, email.Trim(), name ?? user);

        logger.LogInformation("Bootstrap: materialising first admin '{User}' ({Email})", user, email);

        // Idempotent step runner: "already exists" is success (a pre-existing static/seed node
        // is fine — we still want the Admin grants). Any other error is a real failure.
        async Task<bool> Step(IObservable<MeshWeaver.Mesh.MeshNode> obs, string step)
        {
            try
            {
                await obs.FirstAsync().ToTask();
                logger.LogInformation("Bootstrap: {Step} OK for '{User}'", step, user);
                return true;
            }
            catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Bootstrap: {Step} already present for '{User}' — continuing", step, user);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Bootstrap: {Step} FAILED for '{User}'", step, user);
                return false;
            }
        }

        // Create the user node if missing (tolerate a pre-existing static/seed node), then grant
        // self-Admin + platform-Admin so the resolved identity has Admin everywhere it needs it.
        await Step(onboarding.CreateUser(request), "create-user");
        var selfOk = await Step(onboarding.GrantSelfAdmin(user), "self-admin");
        var platOk = await Step(onboarding.GrantPlatformAdmin(user), "platform-admin");

        return selfOk && platOk
            ? Ok($"OK: '{user}' ({email}) is platform admin. Sign in via Microsoft.")
            : StatusCode(500, $"PARTIAL: self-admin={selfOk} platform-admin={platOk} — check portal logs.");
    }
}
