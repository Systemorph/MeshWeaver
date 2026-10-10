using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fido2NetLib;
using MeshWeaver.AI;   // IProviderKeyProtector keeps its original namespace (#2398 forwarders)
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
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
///
/// <para><b>What every completing endpoint checks, in this order:</b> the pending step-up is TAKEN
/// atomically (one ceremony, one proof); the rung the server recorded on it is this endpoint's
/// method AND the ladder decided again now still lands on it (<see cref="StepUpLadder.MayComplete"/>
/// — so a Microsoft account can never finish an Entra step-up with a portal factor); and a TOTP
/// step or recovery code is CLAIMED in the store before a receipt is minted, so two concurrent
/// confirmations of one code yield one receipt, not two.</para>
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
    public Task<IActionResult> PasskeyOptions(CancellationToken ct = default)
    {
        var t = Texts();
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Done(Unauthorized());
        var rung = RungCheck();
        var rp = Passkeys();
        var flow = TakePending(userId, state: null, complete: false).SelectMany(pending => pending is null
            ? Observable.Return<IActionResult>(Json(Error(t, "state")))
            : ReadFactors(userId).Select(read =>
            {
                if (!read.Answered) return Json(Error(t, "unavailable"));
                if (!rung(pending, StepUpRung.Passkey, read.Factors)) return Json(Error(t, "wrongRung"));
                if (read.Factors is not { Passkeys.Count: > 0 } factors) return Json(Error(t, "notEnrolled"));
                var options = rp.AssertionOptions(PasskeyStepUp.ChallengeFor(userId, pending.Targets, pending.Nonce), factors.Passkeys.Values);
                return (IActionResult)Content(options.ToJson(), "application/json");
            }));
        return Edge(flow, () => Json(Error(t, "unavailable")), "passkey options", userId, ct);
    }

    /// <summary>Verifies the assertion, mints the receipt, stamps the targets.</summary>
    /// <param name="assertion">The browser's assertion response (the request body).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><c>{redirect}</c> or <c>{error}</c>.</returns>
    [HttpPost("passkey/verify")]
    public Task<IActionResult> PasskeyVerify([FromBody] JsonElement assertion, CancellationToken ct = default)
    {
        var t = Texts();
        var caller = access.Context;
        var userId = caller?.ObjectId;
        if (caller is null || string.IsNullOrEmpty(userId)) return Done(Unauthorized());
        var hub = Hub();
        if (hub is null) return Done(Json(Error(t, "mint")));
        var body = assertion.GetRawText();
        var rung = RungCheck();
        var rp = Passkeys(hub);

        var flow = TakePending(userId, state: null, complete: true).SelectMany(pending =>
        {
            if (pending is null) return Observable.Return<IActionResult>(Json(Error(t, "state")));
            return ReadFactors(userId).SelectMany(read =>
            {
                if (!read.Answered) return Observable.Return<IActionResult>(Json(Error(t, "unavailable")));
                // Server-decided, re-checked: only a step-up STARTED as a passkey one, for an
                // account whose rung is still the passkey (never a Microsoft account).
                if (!rung(pending, StepUpRung.Passkey, read.Factors))
                {
                    logger.LogWarning("Step-up passkey for {User} refused: the step-up was started as '{Rung}'", userId, pending.Rung);
                    return Observable.Return<IActionResult>(Json(Error(t, "wrongRung")));
                }
                if (read.Factors is not { Passkeys.Count: > 0 } factors)
                    return Observable.Return<IActionResult>(Json(Error(t, "notEnrolled")));
                var options = rp.AssertionOptions(PasskeyStepUp.ChallengeFor(userId, pending.Targets, pending.Nonce), factors.Passkeys.Values);
                var now = DateTimeOffset.UtcNow;
                return rp.Assert(body, options, factors.Passkeys.Values, now).SelectMany(result =>
                {
                    if (!result.Ok || result.Credential is not { } used)
                    {
                        logger.LogWarning("Step-up passkey for {User} refused: {Reason}", userId, result.Reason);
                        return Observable.Return<IActionResult>(Json(Error(t, result.Reason ?? "passkey")));
                    }
                    // The snapshot only says the counter COULD be accepted. A non-zero counter value is
                    // CLAIMED in the store first, so two assertions carrying the same counter (a cloned
                    // authenticator, run against two ceremonies at once) yield one receipt, not two.
                    // Zero means "no counter" (WebAuthn): nothing to claim, the single-use pending bounds it.
                    var claim = used.SignCount > 0
                        ? new StepUpSingleUse(hub).ClaimPasskeyCounter(userId, used.CredentialId, used.SignCount)
                        : Observable.Return(true);
                    return claim.SelectMany(won =>
                    {
                        if (!won)
                        {
                            logger.LogWarning("Step-up passkey for {User} refused: counter {Count} of the credential was already spent", userId, used.SignCount);
                            return Observable.Return<IActionResult>(Json(Error(t, "counter")));
                        }
                        return new StepUpFactorStore(hub)
                            // Keyed by credential id: the patch touches this credential's fields alone.
                            .Update(userId, f => f with
                            {
                                Passkeys = f.Passkeys.TryGetValue(used.CredentialId, out var current)
                                    ? f.Passkeys.SetItem(used.CredentialId, used with { SignCount = Math.Max(used.SignCount, current.SignCount) })
                                    : f.Passkeys,
                            })
                            .SelectMany(_ => MintAndStamp(hub, caller, StepUpMethod.Passkey, now,
                                $"credential={used.CredentialId[..Math.Min(12, used.CredentialId.Length)]} aaguid={used.AaGuid}", pending.Targets))
                            .Select(receipt => (IActionResult)Json(new { redirect = SuccessUrl(pending, receipt) }))
                            .Catch((Exception ex) =>
                            {
                                logger.LogWarning(ex, "Step-up passkey for {User}: recording the receipt failed", userId);
                                return Observable.Return<IActionResult>(Json(Error(t, "mint")));
                            });
                    });
                });
            });
        });
        return Edge(flow, () => Json(Error(t, "mint")), "passkey verify", userId, ct);
    }

    // ───────────────────────────── the TOTP rung ─────────────────────────────

    /// <summary>Verifies a TOTP or recovery code for the pending step-up — only for an account with no passkey.</summary>
    /// <param name="code">What the user typed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A redirect back, or a page explaining the failure.</returns>
    [HttpPost(TotpVerifyAction)]
    public Task<IActionResult> TotpVerify([FromForm] string? code, CancellationToken ct = default)
    {
        var t = Texts();
        var caller = access.Context;
        var userId = caller?.ObjectId;
        if (caller is null || string.IsNullOrEmpty(userId)) return Done(Unauthorized());
        var hub = Hub();
        var rung = RungCheck();

        var flow = TakePending(userId, state: null, complete: true).SelectMany(pending =>
        {
            if (pending is null) return Observable.Return<IActionResult>(Failure(t, "/", "state"));
            if (hub is null) return Observable.Return<IActionResult>(Failure(t, pending.ReturnUrl, "mint"));
            return ReadFactors(userId).SelectMany(read =>
            {
                if (!read.Answered) return Observable.Return<IActionResult>(Failure(t, pending.ReturnUrl, "unavailable"));
                // Re-decided here, never trusted from the page: the step-up was started as a TOTP one,
                // and the account's rung is STILL TOTP — not Entra (a Microsoft account), not a passkey.
                if (!rung(pending, StepUpRung.Totp, read.Factors))
                {
                    logger.LogWarning("Step-up TOTP for {User} refused: the step-up was started as '{Rung}'", userId, pending.Rung);
                    return Observable.Return<IActionResult>(Failure(t, pending.ReturnUrl,
                        read.Factors is { Passkeys.Count: > 0 } ? "downgrade" : "wrongRung"));
                }
                // A durable, per-user attempt budget ACROSS ceremonies, spent before the code is
                // even looked at: consuming the pending record allows one guess per ceremony, but
                // ceremonies are free to start, so without this a stolen session could keep guessing
                // six digits. Each attempt claims one of TotpAttemptsPerWindow slots of the current
                // window in the store (atomic, survives restarts and replicas); none left = refused.
                return new StepUpSingleUse(hub).ClaimTotpAttempt(userId, DateTimeOffset.UtcNow).SelectMany(admitted =>
                {
                    if (!admitted)
                    {
                        logger.LogWarning("Step-up TOTP for {User} refused: the attempt budget of the current window is spent", userId);
                        return Observable.Return<IActionResult>(Failure(t, pending.ReturnUrl, "locked"));
                    }
                    var factors = read.Factors!;
                    var protector = hub.ServiceProvider.GetRequiredService<IProviderKeyProtector>();
                    var secretText = protector.Unprotect(factors.TotpSecretProtected);
                    if (string.IsNullOrEmpty(secretText)) return Observable.Return<IActionResult>(Failure(t, pending.ReturnUrl, "totp"));
                    var secret = Convert.FromBase64String(secretText);
                    var now = DateTimeOffset.UtcNow;

                    var step = Totp.Verify(secret, code, now, factors.LastTotpStep);
                    var recoveryHash = step is null ? Totp.HashRecoveryCode(code ?? "") : null;
                    if (step is null && (recoveryHash is null || !factors.RecoveryCodeHashes.Contains(recoveryHash)))
                    {
                        logger.LogWarning("Step-up TOTP for {User} refused: no valid code", userId);
                        return Observable.Return<IActionResult>(Failure(t, pending.ReturnUrl, "totp"));
                    }

                    // The snapshot above only says the code COULD be accepted. The claim says it IS —
                    // once: of two concurrent confirmations of the same step or recovery code, only the
                    // store's winner goes on to mint.
                    var singleUse = new StepUpSingleUse(hub);
                    var claim = step is { } s ? singleUse.ClaimTotpStep(userId, s) : singleUse.ClaimRecoveryCode(userId, recoveryHash!);
                    return claim.SelectMany(won =>
                    {
                        if (!won)
                        {
                            logger.LogWarning("Step-up TOTP for {User} refused: the code was spent by a concurrent confirmation", userId);
                            return Observable.Return<IActionResult>(Failure(t, pending.ReturnUrl, "totp"));
                        }
                        return new StepUpFactorStore(hub)
                            .Update(userId, f => step is { } s1
                                ? f with { LastTotpStep = Math.Max(f.LastTotpStep, s1) }
                                : f with { RecoveryCodeHashes = f.RecoveryCodeHashes.Remove(recoveryHash!) })
                            .SelectMany(_ => MintAndStamp(hub, caller, StepUpMethod.Totp, now,
                                step is { } s2 ? $"totp step={s2}" : "totp recovery-code", pending.Targets))
                            .Select(receipt => (IActionResult)Redirect(SuccessUrl(pending, receipt)))
                            .Catch((Exception ex) =>
                            {
                                logger.LogWarning(ex, "Step-up TOTP for {User}: recording the receipt failed", userId);
                                return Observable.Return<IActionResult>(Failure(t, pending.ReturnUrl, "mint"));
                            });
                    });
                });
            });
        });
        return Edge(flow, () => Failure(t, "/", "mint"), "totp verify", userId, ct);
    }

    // ───────────────────────────── enrolment ─────────────────────────────

    /// <summary>The enrolment page.</summary>
    /// <param name="returnUrl">Where "back" goes.</param>
    /// <param name="stepUpReceipt">The receipt of a step-up that authorized adding another factor.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page.</returns>
    [HttpGet("enroll")]
    public Task<IActionResult> Enroll([FromQuery] string? returnUrl, [FromQuery] string? stepUpReceipt, CancellationToken ct = default)
    {
        var t = Texts();
        var safeReturn = ReturnUrlPolicy.Sanitize(returnUrl);
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Done(Unauthorized());
        // A Microsoft account confirms through Entra: there is nothing to enrol, and no button to press.
        if (!StepUpLadder.MayEnrollPortalFactors(StepUpClaims.ProviderOf(User)))
            return Done(Page(StepUpPages.Message(t, t.L(StepUpLadder.IsMicrosoft(StepUpClaims.ProviderOf(User))
                ? "stepUp.enroll.microsoft" : "stepUp.refused.unknownSession"), safeReturn)));
        var options = StepUpOptions.From(configuration);
        var gate = EnrollGateFor(userId);
        var flow = ReadFactors(userId).SelectMany(read => !read.Answered
            ? Observable.Return<IActionResult>(Failure(t, safeReturn, "unavailable"))
            : gate(read.Factors, stepUpReceipt, false).Select(authorization =>
            {
                var confirmFirst = authorization == EnrollAuthorization.NeedsStepUp
                    ? StepUpPaths.StartUrl([new StepUpTarget { ActionPath = StepUpPaths.Factors(userId), Binding = StepUpPaths.EnrollBinding }],
                        EnrollPath + "?returnUrl=" + Uri.EscapeDataString(safeReturn))
                    : null;
                return (IActionResult)Page(StepUpPages.Enroll(t, read.Factors, StepUpLadder.MayEnrollTotp(options, read.Factors),
                    authorization == EnrollAuthorization.AllowedByReceipt ? stepUpReceipt : null, confirmFirst,
                    authorization == EnrollAuthorization.NeedsFreshSignIn, safeReturn));
            }));
        return Edge(flow, () => Failure(t, safeReturn, "unavailable"), "enroll page", userId, ct);
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
    public Task<IActionResult> PasskeyRegisterOptions([FromBody] EnrollRequest request, CancellationToken ct = default)
    {
        var t = Texts();
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Done(Unauthorized());
        var gate = EnrollGateFor(userId);
        var display = User.FindFirst("email")?.Value ?? userId;
        var rp = Passkeys();
        var protector = Protector();
        var flow = ReadFactors(userId).SelectMany(read => !read.Answered
            ? Observable.Return<IActionResult>(Json(Error(t, "unavailable")))
            : gate(read.Factors, request.Receipt, false).Select(authorization =>
            {
                if (!IsAllowed(authorization)) return Json(Error(t, ReasonFor(authorization)));
                var options = rp.RegistrationOptions(userId, display, read.Factors?.Passkeys.Values ?? []);
                Response.Cookies.Append(RegistrationCookie, protector.Protect(BindToUser(userId, options.ToJson()), DateTimeOffset.UtcNow + PendingLifetime), SealedCookie());
                return (IActionResult)Content(options.ToJson(), "application/json");
            }));
        return Edge(flow, () => Json(Error(t, "unavailable")), "passkey register options", userId, ct);
    }

    /// <summary>Verifies the attestation and stores the new passkey.</summary>
    /// <param name="request">The receipt and the attestation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><c>{ok}</c> or <c>{error}</c>.</returns>
    [HttpPost("passkey/register")]
    public Task<IActionResult> PasskeyRegister([FromBody] EnrollRequest request, CancellationToken ct = default)
    {
        var t = Texts();
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Done(Unauthorized());
        var hub = Hub();
        if (hub is null) return Done(Json(Error(t, "mint")));
        var optionsJson = ReadSealedFor(RegistrationCookie, userId);
        Response.Cookies.Delete(RegistrationCookie, new CookieOptions { Path = "/" + BasePath });
        if (optionsJson is null) return Done(Json(Error(t, "state")));
        var gate = EnrollGateFor(userId);
        var rp = Passkeys(hub);
        var store = new StepUpFactorStore(hub);

        var flow = ReadFactors(userId).SelectMany(read => !read.Answered
            ? Observable.Return<IActionResult>(Json(Error(t, "unavailable")))
            // The authorization is CONSUMED here, at the write — options alone grant nothing.
            : gate(read.Factors, request.Receipt, true).SelectMany(authorization => !IsAllowed(authorization)
                ? Observable.Return<IActionResult>(Json(Error(t, ReasonFor(authorization))))
                : rp.Register(request.Credential ?? "", CredentialCreateOptions.FromJson(optionsJson), read.Factors?.Passkeys.Values ?? [], DateTimeOffset.UtcNow)
                    .SelectMany(result =>
                    {
                        if (!result.Ok || result.Credential is not { } credential)
                            return Observable.Return<IActionResult>(Json(Error(t, result.Reason ?? "passkey")));
                        return StoreFactor(store, userId, authorization,
                                first: new StepUpFactors { Passkeys = ImmutableDictionary<string, PasskeyCredential>.Empty.Add(credential.CredentialId, credential) },
                                fold: f => f.Passkeys.ContainsKey(credential.CredentialId) ? f : f with { Passkeys = f.Passkeys.Add(credential.CredentialId, credential) },
                                landed: f => f.Passkeys.ContainsKey(credential.CredentialId),
                                report: ex => logger.LogWarning(ex, "Passkey enrolment for {User} could not be stored", userId))
                            .Select(outcome =>
                            {
                                logger.LogInformation("Passkey enrolment for {User}: {Outcome}", userId, outcome);
                                return outcome == FactorWrite.Stored ? Json(new { ok = true }) : Json(Error(t, ReasonFor(outcome)));
                            });
                    })));
        return Edge(flow, () => Json(Error(t, "mint")), "passkey register", userId, ct);
    }

    /// <summary>Starts an authenticator-app enrolment — only for an account with no passkey.</summary>
    /// <param name="request">The authorizing receipt.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><c>{svg, secret}</c> or <c>{error}</c>.</returns>
    [HttpPost("totp/enroll/start")]
    public Task<IActionResult> TotpEnrollStart([FromBody] EnrollRequest request, CancellationToken ct = default)
    {
        var t = Texts();
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Done(Unauthorized());
        var gate = EnrollGateFor(userId);
        var options = StepUpOptions.From(configuration);
        var account = User.FindFirst("email")?.Value ?? userId;
        var host = Request.Host.Host;
        var protector = Protector();
        var flow = ReadFactors(userId).SelectMany(read =>
        {
            if (!read.Answered) return Observable.Return<IActionResult>(Json(Error(t, "unavailable")));
            if (!StepUpLadder.MayEnrollTotp(options, read.Factors)) return Observable.Return<IActionResult>(Json(Error(t, "downgrade")));
            return gate(read.Factors, request.Receipt, false).Select(authorization =>
            {
                if (!IsAllowed(authorization)) return Json(Error(t, ReasonFor(authorization)));
                var secret = Totp.NewSecret();
                Response.Cookies.Append(TotpEnrollCookie, protector.Protect(BindToUser(userId, Convert.ToBase64String(secret)), DateTimeOffset.UtcNow + PendingLifetime), SealedCookie());
                var uri = Totp.OtpAuthUri(host, account, secret);
                return (IActionResult)Json(new { svg = QrCode.EncodeText(uri, QrCode.Ecc.Medium).ToSvgString(4), secret = Totp.Base32(secret) });
            });
        });
        return Edge(flow, () => Json(Error(t, "unavailable")), "totp enroll start", userId, ct);
    }

    /// <summary>Confirms the authenticator app with its first code; returns the recovery codes ONCE.</summary>
    /// <param name="request">The receipt and the code.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><c>{recoveryCodes}</c> or <c>{error}</c>.</returns>
    [HttpPost("totp/enroll/confirm")]
    public Task<IActionResult> TotpEnrollConfirm([FromBody] EnrollRequest request, CancellationToken ct = default)
    {
        var t = Texts();
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Done(Unauthorized());
        var hub = Hub();
        if (hub is null) return Done(Json(Error(t, "mint")));
        var secretText = ReadSealedFor(TotpEnrollCookie, userId);
        if (secretText is null) return Done(Json(Error(t, "state")));
        var gate = EnrollGateFor(userId);
        var options = StepUpOptions.From(configuration);
        var store = new StepUpFactorStore(hub);
        var keyProtector = hub.ServiceProvider.GetRequiredService<IProviderKeyProtector>();

        var flow = ReadFactors(userId).SelectMany(read =>
        {
            if (!read.Answered) return Observable.Return<IActionResult>(Json(Error(t, "unavailable")));
            if (!StepUpLadder.MayEnrollTotp(options, read.Factors)) return Observable.Return<IActionResult>(Json(Error(t, "downgrade")));
            var step = Totp.Verify(Convert.FromBase64String(secretText), request.Code, DateTimeOffset.UtcNow, lastAcceptedStep: 0);
            if (step is null) return Observable.Return<IActionResult>(Json(Error(t, "totp")));
            return gate(read.Factors, request.Receipt, true).SelectMany(authorization =>
            {
                if (!IsAllowed(authorization)) return Observable.Return<IActionResult>(Json(Error(t, ReasonFor(authorization))));
                Response.Cookies.Delete(TotpEnrollCookie, new CookieOptions { Path = "/" + BasePath });
                var codes = Totp.NewRecoveryCodes();
                var protectedSecret = keyProtector.Protect(secretText);
                StepUpFactors Apply(StepUpFactors f) => f with
                {
                    TotpSecretProtected = protectedSecret,
                    TotpConfirmedAt = DateTimeOffset.UtcNow,
                    LastTotpStep = step.Value,
                    RecoveryCodeHashes = codes.Select(Totp.HashRecoveryCode).ToImmutableList(),
                };
                return StoreFactor(store, userId, authorization,
                        first: Apply(new StepUpFactors()),
                        fold: Apply,
                        landed: f => f.TotpSecretProtected == protectedSecret,
                        report: ex => logger.LogWarning(ex, "TOTP enrolment for {User} could not be stored", userId))
                    .Select(outcome =>
                    {
                        logger.LogInformation("TOTP enrolment for {User}: {Outcome}", userId, outcome);
                        return outcome == FactorWrite.Stored ? Json(new { recoveryCodes = codes }) : Json(Error(t, ReasonFor(outcome)));
                    });
            });
        });
        return Edge(flow, () => Json(Error(t, "mint")), "totp enroll confirm", userId, ct);
    }

    // ───────────────────────────── shared ─────────────────────────────

    /// <summary>Whether adding a factor is authorized right now — and WHICH write it authorizes.</summary>
    internal enum EnrollAuthorization
    {
        /// <summary>No factor exists and the sign-in is fresh: the FIRST factor may be CREATED — never added to an existing node.</summary>
        AllowedFirst,

        /// <summary>A factor exists and a step-up (or, with step-up off, a fresh sign-in) authorized adding another.</summary>
        AllowedByReceipt,

        /// <summary>A factor exists — confirm with it first.</summary>
        NeedsStepUp,

        /// <summary>No factor exists and the sign-in is not recent enough to enrol the first one.</summary>
        NeedsFreshSignIn,

        /// <summary>A Microsoft account (or a session that does not name its provider): portal factors are not for it.</summary>
        NotForThisAccount,
    }

    /// <summary>What a factor write came to.</summary>
    internal enum FactorWrite
    {
        /// <summary>Stored, and the stored node carries this write.</summary>
        Stored,

        /// <summary>A first-factor create found a node already there (a stale listing or a concurrent enrolment) — refused.</summary>
        AlreadyEnrolled,

        /// <summary>The store did not take it.</summary>
        Failed,
    }

    private static bool IsAllowed(EnrollAuthorization a) => a is EnrollAuthorization.AllowedFirst or EnrollAuthorization.AllowedByReceipt;

    private static string ReasonFor(EnrollAuthorization a) =>
        a == EnrollAuthorization.NotForThisAccount ? "microsoftAccount" : "enrollAuth";

    private static string ReasonFor(FactorWrite w) => w == FactorWrite.AlreadyEnrolled ? "enrollAuth" : "mint";

    /// <summary>
    /// Writes a factor the way <paramref name="authorization"/> allowed — and no other way. A FIRST
    /// factor is a CREATE that refuses an existing node (<see cref="StepUpFactorStore.Create"/>) and
    /// then checks the node it reads back carries THIS write (<paramref name="landed"/>); a further
    /// factor is a fold onto the existing node. A stale "no factors" listing therefore can never add
    /// a factor beside an existing one without the step-up that adding one requires.
    /// </summary>
    internal static IObservable<FactorWrite> StoreFactor(StepUpFactorStore store, string userId, EnrollAuthorization authorization,
        StepUpFactors first, Func<StepUpFactors, StepUpFactors> fold, Func<StepUpFactors, bool> landed, Action<Exception> report) =>
        (authorization switch
        {
            EnrollAuthorization.AllowedFirst => store.Create(userId, first)
                .Select(stored => stored is null ? FactorWrite.AlreadyEnrolled
                    : landed(stored) ? FactorWrite.Stored : FactorWrite.AlreadyEnrolled),
            EnrollAuthorization.AllowedByReceipt => store.Update(userId, fold).Select(_ => FactorWrite.Stored),
            _ => Observable.Return(FactorWrite.Failed),
        })
        .Catch((Exception ex) =>
        {
            report(ex);
            return Observable.Return(FactorWrite.Failed);
        });

    /// <summary>
    /// The enrolment gate, with everything it needs from the request captured NOW (at the action's
    /// edge): a Microsoft account — or a session that does not name its provider — enrols nothing;
    /// the FIRST factor needs a sign-in within <see cref="FirstFactorSignInAge"/>; every further one
    /// needs a step-up with an existing factor — so a stolen session cannot add its own
    /// authenticator. With step-up off on the instance the fresh sign-in is the bar for both.
    /// </summary>
    private Func<StepUpFactors?, string?, bool, IObservable<EnrollAuthorization>> EnrollGateFor(string userId)
    {
        var provider = StepUpClaims.ProviderOf(User);
        var signedInRecently = SignedInRecently();
        var enabled = StepUpOptions.From(configuration).Enabled;
        var service = Hub()?.ServiceProvider.GetRequiredService<IStepUpService>();
        return (factors, receiptId, consume) =>
        {
            if (!StepUpLadder.MayEnrollPortalFactors(provider))
                return Observable.Return(EnrollAuthorization.NotForThisAccount);
            var hasFactor = factors is { Passkeys.Count: > 0 } || factors is { TotpConfirmedAt: not null };
            if (!hasFactor)
                return Observable.Return(signedInRecently ? EnrollAuthorization.AllowedFirst : EnrollAuthorization.NeedsFreshSignIn);
            if (string.IsNullOrEmpty(receiptId))
                return Observable.Return(enabled || !signedInRecently ? EnrollAuthorization.NeedsStepUp : EnrollAuthorization.AllowedByReceipt);
            if (service is null) return Observable.Return(EnrollAuthorization.NeedsStepUp);
            return (consume
                    ? service.Consume(receiptId, userId, StepUpPaths.Factors(userId), StepUpPaths.EnrollBinding)
                    : service.Check(receiptId, userId, StepUpPaths.Factors(userId), StepUpPaths.EnrollBinding))
                .Select(verdict => verdict.Outcome == StepUpOutcome.Accepted
                                   || (verdict.Outcome == StepUpOutcome.NotRequired && signedInRecently)
                    ? EnrollAuthorization.AllowedByReceipt
                    : EnrollAuthorization.NeedsStepUp);
        };
    }

    /// <summary>
    /// The completion check of <see cref="StepUpLadder.MayComplete"/>, with the session's provider,
    /// the instance options and the Entra configuration captured now.
    /// </summary>
    private Func<StepUpPending, string, StepUpFactors?, bool> RungCheck()
    {
        var provider = StepUpClaims.ProviderOf(User);
        var options = StepUpOptions.From(configuration);
        var entra = Entra();
        var entraUsable = entra.IsConfigured && entra.TenantIsSpecific;
        return (pending, method, factors) => StepUpLadder.MayComplete(pending.Rung, method, provider, options, entraUsable, factors);
    }

    /// <summary>The session's sign-in is within <see cref="FirstFactorSignInAge"/> (its <c>auth_time</c> claim).</summary>
    private bool SignedInRecently() =>
        long.TryParse(User.FindFirst(StepUpClaims.AuthTime)?.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var at)
        && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(at) <= FirstFactorSignInAge;

    /// <summary>One factor read: answered (with or without factors) or not.</summary>
    internal readonly record struct FactorRead(bool Answered, StepUpFactors? Factors);

    private IObservable<FactorRead> ReadFactors(string userId)
    {
        var hub = Hub();
        if (hub is null) return Observable.Return(new FactorRead(false, null));
        return new StepUpFactorStore(hub).Load(userId)
            .Select(f => new FactorRead(true, f))
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Step-up: the factor read for {User} did not answer", userId);
                return Observable.Return(new FactorRead(false, null));
            })
            .DefaultIfEmpty(new FactorRead(false, null))
            .Take(1);
    }

    private PasskeyStepUp Passkeys(IMessageHub? hub = null)
    {
        var resolved = hub ?? Hub();
        // The FIDO library's verification is a Task-returning edge; it runs through the mesh's pool,
        // and a hub without the pool registry FAILS (GetRequiredService) rather than verifying
        // unbounded. Only the no-hub path, which builds options and never verifies, has no pool.
        var pool = resolved is null
            ? IoPool.Unbounded
            : resolved.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.Http);
        return new(Request.Host.Host, $"{Request.Scheme}://{Request.Host}", Request.Host.Host, pool);
    }

    /// <summary>
    /// Binds a sealed enrolment payload to the account that requested it. The cookie outlives a
    /// sign-out (only the authentication cookie is removed), so without the binding a payload
    /// started as account A could be confirmed after a fresh sign-in as account B and stored as B's
    /// factor — A's WebAuthn user handle, or a TOTP secret A was shown.
    /// </summary>
    internal static string BindToUser(string userId, string payload) => userId + "\n" + payload;

    /// <summary>The payload of a <see cref="BindToUser"/> text when it names <paramref name="userId"/>; otherwise null.</summary>
    internal static string? UnbindFromUser(string boundText, string userId)
    {
        var cut = boundText.IndexOf('\n');
        return cut > 0 && string.Equals(boundText[..cut], userId, StringComparison.Ordinal)
            ? boundText[(cut + 1)..]
            : null;
    }

    /// <summary>The sealed payload of <paramref name="cookie"/> when it was bound to <paramref name="userId"/>; otherwise null.</summary>
    private string? ReadSealedFor(string cookie, string userId)
    {
        var sealedText = ReadSealed(cookie);
        if (sealedText is null) return null;
        if (UnbindFromUser(sealedText, userId) is { } payload) return payload;
        logger.LogWarning("Step-up: cookie {Cookie} was sealed for another account; refused", cookie);
        return null;
    }

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

    private static object Error(StepUpPageTexts t, string reason) => new { error = t.L("stepUp.failed", t.L("stepUp.reason." + reason)) };

    private static JsonResult Json(object value) => new(value);
}
