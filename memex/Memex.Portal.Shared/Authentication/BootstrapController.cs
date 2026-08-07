using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        // The request token rides along so a disconnected client does not leave the write running.
        async Task<bool> Step(IObservable<MeshWeaver.Mesh.MeshNode> obs, string step)
        {
            try
            {
                await obs.FirstAsync().ToTask(HttpContext.RequestAborted);
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

    /// <summary>
    /// Secret-gated headless mint of a registration bootstrap key (<c>mwr_…</c>) — the same
    /// operation the Instance grants tab performs, for environments with no drivable UI (scripted
    /// scaffolds, the e2e stack). Same gate and lifecycle as <see cref="FirstAdmin"/>: disabled
    /// without <c>Bootstrap:Secret</c>, meant for one-shot operator use. The minted key's OWNER is
    /// <paramref name="username"/> — instances registered with it land in that user's partition,
    /// exactly as if they had minted it in the tab (never more access than that user's own
    /// self-service registration would grant).
    /// </summary>
    [HttpGet("registration-key")]
    [HttpPost("registration-key")]
    public async Task<IActionResult> RegistrationKey(
        [FromQuery] string? secret,
        [FromQuery] string? username,
        [FromQuery] string? name,
        [FromQuery] string? email,
        [FromQuery] string? description,
        [FromServices] IMessageHub hub)
    {
        var expected = config["Bootstrap:Secret"];
        if (string.IsNullOrWhiteSpace(expected))
            return NotFound();                       // disabled unless a secret is configured
        if (!string.Equals(secret, expected, StringComparison.Ordinal))
            return Unauthorized("invalid or missing secret");
        if (string.IsNullOrWhiteSpace(username))
            return BadRequest("username query parameter is required (the minted key's owner)");

        var owner = username.Trim().ToLowerInvariant();
        var keys = hub.ServiceProvider.GetRequiredService<RegistrationKeyService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

        // 🚨 The response body IS a one-time secret. Forbid caching anywhere on the path — a proxy
        // or browser cache (or a prefetch of the GET form) would hand the raw key to whoever asks
        // next. GET is kept because scripted scaffolds and `curl` in a k8s one-shot pod use it, so
        // the no-store header is what makes that shape safe rather than the verb.
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers.Pragma = "no-cache";

        logger.LogInformation("Bootstrap: minting registration key owned by '{Owner}'", owner);

        try
        {
            // Mint under the OWNER's identity — the key node lands in their partition, the same
            // write the tab performs for a signed-in admin. The Bootstrap secret is the authority
            // here, exactly as it is for first-admin.
            // The request's cancellation token rides along: a disconnected client must not leave
            // the mint running in the background.
            var result = await Observable.Using(
                    () => accessService.SwitchAccessContext(new AccessContext
                    {
                        ObjectId = owner,
                        Name = string.IsNullOrWhiteSpace(name) ? owner : name.Trim(),
                        Email = email?.Trim() ?? "",
                    }),
                    _ => keys.Mint(owner, name?.Trim() ?? owner, email?.Trim() ?? "",
                        description?.Trim() ?? "bootstrap-minted"))
                .FirstAsync().ToTask(HttpContext.RequestAborted);

            // The raw key IS the response body — shown once, never stored, same contract as the tab.
            return Ok(result.RawKey);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bootstrap: minting registration key for '{Owner}' FAILED", owner);
            return StatusCode(500, $"Minting failed: {ex.Message} — check portal logs.");
        }
    }
}
