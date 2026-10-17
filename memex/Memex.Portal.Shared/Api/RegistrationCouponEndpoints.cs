using System.Globalization;
using System.Reactive.Linq;
using System.Text.Json;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The wire shape of the registry's COUPON mint (<see cref="RegistrationCouponEndpoints"/>). The
/// consumer is the hosting operator's shell (<c>hosting-registry-coupon</c>), which reads
/// <c>coupon</c>, <c>tier</c> and <c>id</c> with <c>jq</c>; renaming a field breaks it silently.
/// </summary>
public static class RegistrationCouponPayloads
{
    /// <summary><c>POST</c> <see cref="Request"/> — mint one coupon.</summary>
    public const string Route = "/api/instances/coupons";

    /// <summary>The header the mint credential is presented in — never a query parameter.</summary>
    public const string SecretHeader = "X-MeshWeaver-Coupon-Secret";

    /// <summary>The registry's configuration key holding the mint credential. Unset ⇒ the route does not exist (404).</summary>
    public const string SecretConfigKey = "PluginCatalog:Coupons:MintSecret";

    /// <summary>The registry's configuration key naming the user (partition) that owns minted coupons —
    /// and therefore every instance registered with one, exactly as for a key minted in the tab.</summary>
    public const string OwnerConfigKey = "PluginCatalog:Coupons:Owner";

    /// <summary>The longest a coupon may live. A coupon is minted for a ramp-up that is days away, not a standing credential.</summary>
    public const int MaxExpiresDays = 90;

    /// <summary>The most registrations one coupon may perform.</summary>
    public const int MaxUsagesCeiling = 10;

    /// <summary>A mint request.</summary>
    /// <param name="Tier">The plan the coupon enrols its instance into — an id of the registry's <c>Admin/Tiers/*</c> ladder. Required.</param>
    /// <param name="Description">What it is for — name the client deployment. Required.</param>
    /// <param name="ExpiresDays">Days until it expires, 1–<see cref="MaxExpiresDays"/>. Required.</param>
    /// <param name="MaxUsages">How many registrations it may perform, 1–<see cref="MaxUsagesCeiling"/>; null ⇒ 1.</param>
    public sealed record Request(string? Tier, string? Description, int? ExpiresDays, int? MaxUsages);

    /// <summary>The answer: the raw coupon (shown ONCE — the caller stores it and forgets it) and its facts.</summary>
    /// <param name="Coupon">The raw <c>mwr_</c> key.</param>
    /// <param name="Id">The key node's id (its hash prefix) — what the Registration keys list shows.</param>
    /// <param name="Tier">The canonical plan it carries.</param>
    /// <param name="ExpiresAt">When it expires.</param>
    /// <param name="MaxUsages">How many registrations it may perform.</param>
    public sealed record Response(string Coupon, string Id, string Tier, DateTimeOffset ExpiresAt, int MaxUsages);

    /// <summary>camelCase, so the shell reads <c>.coupon</c>.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Why a request may not be minted, or null — the bounds, as a PURE function. The ladder check
    /// is separate (it needs the registry's tier nodes).
    /// </summary>
    public static string? Refusal(Request? request)
    {
        if (request is null)
            return "a JSON body {tier, description, expiresDays, maxUsages} is required";
        if (PlanTierRanks.Canonical(request.Tier).Length == 0)
            return "tier is required — a coupon without a plan is not a coupon";
        if (string.IsNullOrWhiteSpace(request.Description))
            return "description is required — name the deployment the coupon is for";
        if (request.ExpiresDays is not { } days || days < 1 || days > MaxExpiresDays)
            return $"expiresDays is required, 1–{MaxExpiresDays} — a coupon never lives for ever";
        if (request.MaxUsages is { } uses && (uses < 1 || uses > MaxUsagesCeiling))
            return $"maxUsages must be 1–{MaxUsagesCeiling} (omit it for a single-use coupon)";
        return null;
    }
}

/// <summary>
/// <c>POST /api/instances/coupons</c> — mint a registration COUPON: a plan-bearing, expiring,
/// usage-limited registration key (<see cref="RegistrationKey.MaxUsages"/>) for ONE client
/// deployment, so its Provision registers it on its plan from the beginning instead of on
/// <c>free</c> and a person raising the plan afterwards.
///
/// <para>🚨 <b>A credential scoped to THIS act, not the bootstrap secret.</b> <c>Bootstrap:Secret</c>
/// also materialises a platform administrator (<see cref="BootstrapController"/>), so handing it to
/// an operator lane would hand that lane the registry. The mint credential
/// (<see cref="RegistrationCouponPayloads.SecretConfigKey"/>) can do one thing: mint a coupon that
/// carries a plan the registry's ladder knows, expires within
/// <see cref="RegistrationCouponPayloads.MaxExpiresDays"/> days and registers at most
/// <see cref="RegistrationCouponPayloads.MaxUsagesCeiling"/> instances. Unset ⇒ the route answers 404.</para>
///
/// <para>The coupon is owned by <see cref="RegistrationCouponPayloads.OwnerConfigKey"/> — a platform
/// administrator of the registry — so instances registered with it land in that partition, as for a
/// key minted in Instance grants ▸ Registration keys, and that administrator sees and revokes it there.
/// The raw value is the response body, once, with <c>Cache-Control: no-store</c>.</para>
/// </summary>
public static class RegistrationCouponEndpoints
{
    /// <summary>Maps the coupon mint. Called by <c>MapInstanceRegistration</c>, so every registry host serves it (404 while unconfigured).</summary>
    public static IEndpointRouteBuilder MapRegistrationCoupons(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(RegistrationCouponPayloads.Route, Mint).AllowAnonymous();
        return endpoints;
    }

    private static Task<IResult> Mint(
        HttpContext http, IMessageHub rootHub, RegistrationCouponPayloads.Request? body, CancellationToken ct)
    {
        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(RegistrationCouponEndpoints));
        var config = http.RequestServices.GetService<IConfiguration>() ?? rootHub.ServiceProvider.GetService<IConfiguration>();
        http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
        http.Response.Headers.Pragma = "no-cache";

        switch (BootstrapSecretGate.Decide(config?[RegistrationCouponPayloads.SecretConfigKey],
                    http.Request.Headers[RegistrationCouponPayloads.SecretHeader].ToString()))
        {
            case BootstrapAuthResult.Disabled:
                return Task.FromResult(Results.NotFound());
            case BootstrapAuthResult.Accepted:
                break;
            default:
                logger?.LogWarning("Coupon mint refused: missing or invalid {Header}", RegistrationCouponPayloads.SecretHeader);
                return Task.FromResult(Results.Json(new { error = "invalid or missing mint credential" },
                    statusCode: StatusCodes.Status401Unauthorized));
        }

        var owner = config?[RegistrationCouponPayloads.OwnerConfigKey]?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(owner))
            return Task.FromResult(Results.Json(
                new { error = $"this registry configures no {RegistrationCouponPayloads.OwnerConfigKey} — nobody would own the coupon or the instances it registers. Nothing was minted." },
                statusCode: StatusCodes.Status503ServiceUnavailable));
        if (RegistrationCouponPayloads.Refusal(body) is { } refusal)
            return Task.FromResult(Results.Json(new { error = refusal }, statusCode: StatusCodes.Status400BadRequest));

        var request = body!;
        var tier = PlanTierRanks.Canonical(request.Tier);
        var uses = request.MaxUsages ?? 1;
        var expiresAt = DateTimeOffset.UtcNow.AddDays(request.ExpiresDays!.Value);
        var ladder = rootHub.ServiceProvider.GetService<PlanTierLadder>();
        var keys = rootHub.ServiceProvider.GetRequiredService<RegistrationKeyService>();
        var access = rootHub.ServiceProvider.GetRequiredService<AccessService>();

        return (ladder?.Read().Take(1) ?? Observable.Return(PlanTierRanks.Empty))
            .SelectMany(ranks =>
            {
                // 🚨 The ladder decides, never the caller: a plan the registry does not know licenses
                // nothing (PlanTierRanks — fail closed), so a coupon for it would register an instance
                // that installs nothing. Refused here, before anything is written.
                if (ranks.RankOf(tier) is null)
                    return Observable.Return(Results.Json(new
                    {
                        error = $"plan '{tier}' is not on this registry's tier ladder (Admin/Tiers: "
                                + (ranks.Ids.Count == 0 ? "none seeded" : string.Join(", ", ranks.Ids))
                                + "). Nothing was minted.",
                    }, statusCode: StatusCodes.Status400BadRequest));
                return Observable.Using(
                        () => access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }),
                        _ => keys.Mint(owner, owner, "", request.Description!.Trim(), expiresAt, tier, uses))
                    .Select(result =>
                    {
                        logger?.LogInformation(
                            "Coupon {Id} minted for plan {Tier} ({Uses} use(s), expires {ExpiresAt:u}) — {Description}",
                            result.Node.Id, tier, uses, expiresAt, request.Description);
                        return Results.Json(new RegistrationCouponPayloads.Response(
                                result.RawKey, result.Node.Id, tier, expiresAt, uses),
                            RegistrationCouponPayloads.Json);
                    });
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Coupon mint failed");
                return Observable.Return(Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway));
            })
            .FirstAsync()
            .ObserveCompletion(
                ex => logger?.LogWarning(ex, "Coupon mint faulted after the response had already been sent"),
                ct)!;
    }
}
