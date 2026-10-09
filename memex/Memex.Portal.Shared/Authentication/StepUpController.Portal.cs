using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fido2NetLib;
using MeshWeaver.AI;   // IProviderKeyProtector keeps its original namespace (#2398 forwarders)
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Net.Codecrete.QrCodeGenerator;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The portal-held rungs of the approval step-up — for every account that is NOT a Microsoft
/// account (<c>Doc/Architecture/ApprovalStepUp</c> → "The passkey rung", "The TOTP rung"): the
/// passkey assertion, the TOTP code, and enrolling either. Every endpoint re-decides on the server
/// what the page offered; the page decides nothing.
/// </summary>
public sealed partial class StepUpController
{
    /// <summary>The enrolment page.</summary>
    public const string EnrollPath = "/" + BasePath + "/enroll";

    /// <summary>Where the TOTP form posts.</summary>
    public const string TotpVerifyAction = "totp/verify";

    /// <summary>How recent a sign-in must be to enrol the FIRST factor (no factor exists to step up with).</summary>
    internal static readonly TimeSpan FirstFactorSignInAge = TimeSpan.FromMinutes(10);

    private const string RegistrationCookie = "mw_stepup_reg";
    private const string TotpEnrollCookie = "mw_stepup_totp";

    // ───────────────────────────── the passkey rung ─────────────────────────────

    /// <summary>Assertion options for the pending step-up; the challenge is bound to it.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The options JSON, or <c>{error}</c>.</returns>
    [HttpPost("passkey/options")]
    public async Task<IActionResult> PasskeyOptions(CancellationToken ct = default)
    {
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var pending = await TakePending(userId, state: null, ct, complete: false);
        if (pending is null) return Json(Error("state"));
        var read = await ReadFactors(userId, ct);
        if (!read.Answered) return Json(Error("unavailable"));
        if (read.Factors is not { Passkeys.Count: > 0 } factors) return Json(Error("notEnrolled"));
        var options = Passkeys().AssertionOptions(
            PasskeyStepUp.ChallengeFor(userId, pending.Targets, pending.Nonce), factors.Passkeys);
        return Content(options.ToJson(), "application/json");
    }

    /// <summary>Verifies the assertion, mints the receipt, stamps the targets.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns><c>{redirect}</c> or <c>{error}</c>.</returns>
    [HttpPost("passkey/verify")]
    public async Task<IActionResult> PasskeyVerify(CancellationToken ct = default)
    {
        var caller = access.Context;
        var userId = caller?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var pending = await TakePending(userId, state: null, ct, complete: true);
        if (pending is null) return Json(Error("state"));
        var hub = Hub();
        if (hub is null) return Json(Error("mint"));
        var read = await ReadFactors(userId, ct);
        if (!read.Answered) return Json(Error("unavailable"));
        if (read.Factors is not { Passkeys.Count: > 0 } factors) return Json(Error("notEnrolled"));

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        var rp = Passkeys();
        var options = rp.AssertionOptions(PasskeyStepUp.ChallengeFor(userId, pending.Targets, pending.Nonce), factors.Passkeys);
        var now = DateTimeOffset.UtcNow;
        var result = await rp.Assert(body, options, factors.Passkeys, now, ct);
        if (!result.Ok || result.Credential is not { } used)
        {
            logger.LogWarning("Step-up passkey for {User} refused: {Reason}", userId, result.Reason);
            return Json(Error(result.Reason ?? "passkey"));
        }

        var receipt = await new StepUpFactorStore(hub)
            .Update(userId, f => f with
            {
                Passkeys = f.Passkeys.Select(p => p.CredentialId == used.CredentialId
                    // The counter only ever moves forward, also under a concurrent write.
                    ? used with { SignCount = Math.Max(used.SignCount, p.SignCount) } : p).ToImmutableList(),
            })
            .SelectMany(_ => MintAndStamp(hub, caller!, StepUpMethod.Passkey, now,
                $"credential={used.CredentialId[..Math.Min(12, used.CredentialId.Length)]} aaguid={used.AaGuid}", pending.Targets))
            .Select(r => (StepUpReceipt?)r)
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Step-up passkey for {User}: recording the receipt failed", userId);
                return Observable.Return<StepUpReceipt?>(null);
            })
            .ObserveCompletion(ex => logger.LogWarning(ex, "Step-up passkey for {User} faulted after settling", userId), ct);
        return receipt is null ? Json(Error("mint")) : Json(new { redirect = SuccessUrl(pending, receipt) });
    }

    // ───────────────────────────── the TOTP rung ─────────────────────────────

    /// <summary>Verifies a TOTP or recovery code for the pending step-up — only for an account with no passkey.</summary>
    /// <param name="code">What the user typed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A redirect back, or a page explaining the failure.</returns>
    [HttpPost(TotpVerifyAction)]
    public async Task<IActionResult> TotpVerify([FromForm] string? code, CancellationToken ct = default)
    {
        var caller = access.Context;
        var userId = caller?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var pending = await TakePending(userId, state: null, ct, complete: true);
        if (pending is null) return Failure("/", "state");
        var hub = Hub();
        if (hub is null) return Failure(pending.ReturnUrl, "mint");
        var read = await ReadFactors(userId, ct);
        if (!read.Answered) return Failure(pending.ReturnUrl, "unavailable");
        var options = StepUpOptions.From(configuration);
        // Re-decided here, never trusted from the page: an account with a passkey uses it.
        if (!StepUpLadder.AcceptsTotp(options, read.Factors))
            return Failure(pending.ReturnUrl, read.Factors is { Passkeys.Count: > 0 } ? "downgrade" : "notEnrolled");
        var factors = read.Factors!;
        var protector = hub.ServiceProvider.GetRequiredService<IProviderKeyProtector>();
        var secretText = protector.Unprotect(factors.TotpSecretProtected);
        if (string.IsNullOrEmpty(secretText)) return Failure(pending.ReturnUrl, "totp");
        var secret = Convert.FromBase64String(secretText);
        var now = DateTimeOffset.UtcNow;

        var step = Totp.Verify(secret, code, now, factors.LastTotpStep);
        var recoveryHash = step is null ? Totp.HashRecoveryCode(code ?? "") : null;
        var recovery = recoveryHash is not null && factors.RecoveryCodeHashes.Contains(recoveryHash);
        if (step is null && !recovery)
        {
            logger.LogWarning("Step-up TOTP for {User} refused: no valid code", userId);
            return Failure(pending.ReturnUrl, "totp");
        }

        var receipt = await new StepUpFactorStore(hub)
            .Update(userId, f => step is { } s
                ? f with { LastTotpStep = Math.Max(f.LastTotpStep, s) }
                : f with { RecoveryCodeHashes = f.RecoveryCodeHashes.Remove(recoveryHash!) })
            .SelectMany(_ => MintAndStamp(hub, caller!, StepUpMethod.Totp, now,
                step is { } s2 ? $"totp step={s2}" : "totp recovery-code", pending.Targets))
            .Select(r => (StepUpReceipt?)r)
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Step-up TOTP for {User}: recording the receipt failed", userId);
                return Observable.Return<StepUpReceipt?>(null);
            })
            .ObserveCompletion(ex => logger.LogWarning(ex, "Step-up TOTP for {User} faulted after settling", userId), ct);
        return receipt is null ? Failure(pending.ReturnUrl, "mint") : Redirect(SuccessUrl(pending, receipt));
    }

    // ───────────────────────────── enrolment ─────────────────────────────

    /// <summary>The enrolment page.</summary>
    /// <param name="returnUrl">Where "back" goes.</param>
    /// <param name="stepUpReceipt">The receipt of a step-up that authorized adding another factor.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page.</returns>
    [HttpGet("enroll")]
    public async Task<IActionResult> Enroll([FromQuery] string? returnUrl, [FromQuery] string? stepUpReceipt, CancellationToken ct = default)
    {
        var safeReturn = ReturnUrlPolicy.Sanitize(returnUrl);
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var read = await ReadFactors(userId, ct);
        if (!read.Answered) return Failure(safeReturn, "unavailable");
        var options = StepUpOptions.From(configuration);
        var gate = await EnrollGate(userId, read.Factors, stepUpReceipt, consume: false, ct);
        var confirmFirst = gate == EnrollAuthorization.NeedsStepUp
            ? StepUpPaths.StartUrl([new StepUpTarget { ActionPath = StepUpPaths.Factors(userId), Binding = StepUpPaths.EnrollBinding }],
                EnrollPath + "?returnUrl=" + Uri.EscapeDataString(safeReturn))
            : null;
        return Page(StepUpPages.Enroll(Texts(), read.Factors, StepUpLadder.MayEnrollTotp(options, read.Factors),
            gate == EnrollAuthorization.Allowed ? stepUpReceipt : null, confirmFirst,
            gate == EnrollAuthorization.NeedsFreshSignIn, safeReturn));
    }

    /// <summary>Body of the enrolment calls.</summary>
    public sealed record EnrollRequest
    {
        /// <summary>The authorizing receipt, when another factor exists.</summary>
        [JsonPropertyName("receipt")] public string? Receipt { get; init; }

        /// <summary>The browser's attestation response, as JSON text.</summary>
        [JsonPropertyName("credential")] public string? Credential { get; init; }

        /// <summary>A TOTP code.</summary>
        [JsonPropertyName("code")] public string? Code { get; init; }
    }

    /// <summary>Registration options for a new passkey.</summary>
    /// <param name="request">The authorizing receipt.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The options JSON, or <c>{error}</c>.</returns>
    [HttpPost("passkey/register/options")]
    public async Task<IActionResult> PasskeyRegisterOptions([FromBody] EnrollRequest request, CancellationToken ct = default)
    {
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var read = await ReadFactors(userId, ct);
        if (!read.Answered) return Json(Error("unavailable"));
        if (await EnrollGate(userId, read.Factors, request.Receipt, consume: false, ct) != EnrollAuthorization.Allowed)
            return Json(Error("enrollAuth"));
        var display = User.FindFirst("email")?.Value ?? userId;
        var options = Passkeys().RegistrationOptions(userId, display, read.Factors?.Passkeys ?? []);
        Response.Cookies.Append(RegistrationCookie, Protector().Protect(options.ToJson(), DateTimeOffset.UtcNow + PendingLifetime), SealedCookie());
        return Content(options.ToJson(), "application/json");
    }

    /// <summary>Verifies the attestation and stores the new passkey.</summary>
    /// <param name="request">The receipt and the attestation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><c>{ok}</c> or <c>{error}</c>.</returns>
    [HttpPost("passkey/register")]
    public async Task<IActionResult> PasskeyRegister([FromBody] EnrollRequest request, CancellationToken ct = default)
    {
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var hub = Hub();
        if (hub is null) return Json(Error("mint"));
        var optionsJson = ReadSealed(RegistrationCookie);
        Response.Cookies.Delete(RegistrationCookie, new CookieOptions { Path = "/" + BasePath });
        if (optionsJson is null) return Json(Error("state"));
        var read = await ReadFactors(userId, ct);
        if (!read.Answered) return Json(Error("unavailable"));
        // The authorization is CONSUMED here, at the write — options alone grant nothing.
        if (await EnrollGate(userId, read.Factors, request.Receipt, consume: true, ct) != EnrollAuthorization.Allowed)
            return Json(Error("enrollAuth"));
        var result = await Passkeys().Register(request.Credential ?? "", CredentialCreateOptions.FromJson(optionsJson),
            read.Factors?.Passkeys ?? [], DateTimeOffset.UtcNow, ct);
        if (!result.Ok || result.Credential is not { } credential) return Json(Error(result.Reason ?? "passkey"));
        var stored = await new StepUpFactorStore(hub)
            .Update(userId, f => f.Passkeys.Any(p => p.CredentialId == credential.CredentialId) ? f : f with { Passkeys = f.Passkeys.Add(credential) })
            .Select(_ => true)
            .Catch((Exception ex) => { logger.LogWarning(ex, "Passkey enrolment for {User} could not be stored", userId); return Observable.Return(false); })
            .ObserveCompletion(ex => logger.LogWarning(ex, "Passkey enrolment for {User} faulted after settling", userId), ct);
        logger.LogInformation("Passkey enrolment for {User}: {Outcome}", userId, stored ? "stored" : "failed");
        return stored ? Json(new { ok = true }) : Json(Error("mint"));
    }

    /// <summary>Starts an authenticator-app enrolment — only for an account with no passkey.</summary>
    /// <param name="request">The authorizing receipt.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><c>{svg, secret}</c> or <c>{error}</c>.</returns>
    [HttpPost("totp/enroll/start")]
    public async Task<IActionResult> TotpEnrollStart([FromBody] EnrollRequest request, CancellationToken ct = default)
    {
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var read = await ReadFactors(userId, ct);
        if (!read.Answered) return Json(Error("unavailable"));
        if (!StepUpLadder.MayEnrollTotp(StepUpOptions.From(configuration), read.Factors)) return Json(Error("downgrade"));
        if (await EnrollGate(userId, read.Factors, request.Receipt, consume: false, ct) != EnrollAuthorization.Allowed)
            return Json(Error("enrollAuth"));
        var secret = Totp.NewSecret();
        Response.Cookies.Append(TotpEnrollCookie, Protector().Protect(Convert.ToBase64String(secret), DateTimeOffset.UtcNow + PendingLifetime), SealedCookie());
        var account = User.FindFirst("email")?.Value ?? userId;
        var uri = Totp.OtpAuthUri(Request.Host.Host, account, secret);
        return Json(new { svg = QrCode.EncodeText(uri, QrCode.Ecc.Medium).ToSvgString(4), secret = Totp.Base32(secret) });
    }

    /// <summary>Confirms the authenticator app with its first code; returns the recovery codes ONCE.</summary>
    /// <param name="request">The receipt and the code.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><c>{recoveryCodes}</c> or <c>{error}</c>.</returns>
    [HttpPost("totp/enroll/confirm")]
    public async Task<IActionResult> TotpEnrollConfirm([FromBody] EnrollRequest request, CancellationToken ct = default)
    {
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var hub = Hub();
        if (hub is null) return Json(Error("mint"));
        var secretText = ReadSealed(TotpEnrollCookie);
        if (secretText is null) return Json(Error("state"));
        var read = await ReadFactors(userId, ct);
        if (!read.Answered) return Json(Error("unavailable"));
        if (!StepUpLadder.MayEnrollTotp(StepUpOptions.From(configuration), read.Factors)) return Json(Error("downgrade"));
        var secret = Convert.FromBase64String(secretText);
        var step = Totp.Verify(secret, request.Code, DateTimeOffset.UtcNow, lastAcceptedStep: 0);
        if (step is null) return Json(Error("totp"));
        if (await EnrollGate(userId, read.Factors, request.Receipt, consume: true, ct) != EnrollAuthorization.Allowed)
            return Json(Error("enrollAuth"));
        Response.Cookies.Delete(TotpEnrollCookie, new CookieOptions { Path = "/" + BasePath });

        var protector = hub.ServiceProvider.GetRequiredService<IProviderKeyProtector>();
        var codes = Totp.NewRecoveryCodes();
        var hashes = codes.Select(Totp.HashRecoveryCode).ToImmutableList();
        var stored = await new StepUpFactorStore(hub)
            .Update(userId, f => f with
            {
                TotpSecretProtected = protector.Protect(secretText),
                TotpConfirmedAt = DateTimeOffset.UtcNow,
                LastTotpStep = step.Value,
                RecoveryCodeHashes = hashes,
            })
            .Select(_ => true)
            .Catch((Exception ex) => { logger.LogWarning(ex, "TOTP enrolment for {User} could not be stored", userId); return Observable.Return(false); })
            .ObserveCompletion(ex => logger.LogWarning(ex, "TOTP enrolment for {User} faulted after settling", userId), ct);
        logger.LogInformation("TOTP enrolment for {User}: {Outcome}", userId, stored ? "stored" : "failed");
        return stored ? Json(new { recoveryCodes = codes }) : Json(Error("mint"));
    }

    // ───────────────────────────── shared ─────────────────────────────

    /// <summary>Whether adding a factor is authorized right now.</summary>
    internal enum EnrollAuthorization
    {
        /// <summary>Go ahead.</summary>
        Allowed,

        /// <summary>A factor exists — confirm with it first.</summary>
        NeedsStepUp,

        /// <summary>No factor exists and the sign-in is not recent enough to enrol the first one.</summary>
        NeedsFreshSignIn,
    }

    /// <summary>
    /// The enrolment gate: the FIRST factor needs a sign-in within <see cref="FirstFactorSignInAge"/>;
    /// every further one needs a step-up with an existing factor — so a stolen session cannot add its
    /// own authenticator. With step-up off on the instance the fresh sign-in is the bar for both.
    /// </summary>
    private async Task<EnrollAuthorization> EnrollGate(string userId, StepUpFactors? factors, string? receiptId, bool consume, CancellationToken ct)
    {
        var hasFactor = factors is { Passkeys.Count: > 0 } || factors is { TotpConfirmedAt: not null };
        if (!hasFactor)
            return SignedInRecently() ? EnrollAuthorization.Allowed : EnrollAuthorization.NeedsFreshSignIn;
        if (string.IsNullOrEmpty(receiptId))
            return StepUpOptions.From(configuration).Enabled || !SignedInRecently()
                ? EnrollAuthorization.NeedsStepUp : EnrollAuthorization.Allowed;
        var hub = Hub();
        if (hub is null) return EnrollAuthorization.NeedsStepUp;
        var service = hub.ServiceProvider.GetRequiredService<IStepUpService>();
        var verdict = await (consume
                ? service.Consume(receiptId, userId, StepUpPaths.Factors(userId), StepUpPaths.EnrollBinding)
                : service.Check(receiptId, userId, StepUpPaths.Factors(userId), StepUpPaths.EnrollBinding))
            .ObserveCompletion(ex => logger.LogWarning(ex, "Enrolment gate for {User} faulted after settling", userId), ct);
        return verdict?.Outcome == StepUpOutcome.Accepted
               || (verdict?.Outcome == StepUpOutcome.NotRequired && SignedInRecently())
            ? EnrollAuthorization.Allowed
            : EnrollAuthorization.NeedsStepUp;
    }

    /// <summary>The session's sign-in is within <see cref="FirstFactorSignInAge"/> (its <c>auth_time</c> claim).</summary>
    private bool SignedInRecently() =>
        long.TryParse(User.FindFirst(StepUpClaims.AuthTime)?.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var at)
        && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(at) <= FirstFactorSignInAge;

    /// <summary>One factor read: answered (with or without factors) or not.</summary>
    internal readonly record struct FactorRead(bool Answered, StepUpFactors? Factors);

    private async Task<FactorRead> ReadFactors(string userId, CancellationToken ct)
    {
        var hub = Hub();
        if (hub is null) return new FactorRead(false, null);
        return await new StepUpFactorStore(hub).Load(userId)
            .Select(f => new FactorRead(true, f))
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Step-up: the factor read for {User} did not answer", userId);
                return Observable.Return(new FactorRead(false, null));
            })
            .DefaultIfEmpty(new FactorRead(false, null))
            .ObserveCompletion(ex => logger.LogWarning(ex, "Step-up factor read for {User} faulted after settling", userId), ct);
    }

    private PasskeyStepUp Passkeys() =>
        new(Request.Host.Host, $"{Request.Scheme}://{Request.Host}", Request.Host.Host);

    private string? ReadSealed(string cookie)
    {
        if (!Request.Cookies.TryGetValue(cookie, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try { return Protector().Unprotect(raw); }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "Step-up: cookie {Cookie} did not unprotect (expired or foreign)", cookie);
            return null;
        }
    }

    private static CookieOptions SealedCookie() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = PendingLifetime,
        Path = "/" + BasePath,
    };

    private object Error(string reason) => new { error = access.Localize("stepUp.failed", access.Localize("stepUp.reason." + reason)) };

    private static JsonResult Json(object value) => new(value);
}
