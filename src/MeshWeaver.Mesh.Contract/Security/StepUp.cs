using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// How a step-up was performed — the <c>Method</c> on a <see cref="StepUpReceipt"/>. An OPEN
/// vocabulary (policy <c>open-vocabulary-string-constants</c>): a module may add its own method,
/// and an unknown value is carried as written, never mapped onto one of these.
/// </summary>
public static class StepUpMethod
{
    /// <summary>Entra ID OIDC step-up: <c>prompt=login</c> plus the declared Conditional Access authentication context.</summary>
    public const string Entra = "entra";

    /// <summary>The portal's own WebAuthn passkey assertion (accounts that are not Entra).</summary>
    public const string Passkey = "passkey";

    /// <summary>The portal's own RFC 6238 time-based one-time code — only where no passkey is possible.</summary>
    public const string Totp = "totp";
}

/// <summary>
/// One action a step-up receipt covers: the node path being approved and the hash of WHAT was shown
/// to the approver (an instance action's plan digest, an operation request's script hash, an
/// activity's content hash). A receipt for a bulk approval carries several.
/// </summary>
public sealed record StepUpTarget
{
    /// <summary>The node path of the approval target (the instance action, operation request, activity …).</summary>
    public string ActionPath { get; init; } = "";

    /// <summary>The hash of the thing approved, exactly as the consumer computes it.</summary>
    public string Binding { get; init; } = "";

    /// <summary>The short, stable key of this target inside its receipt — names its consumption marker.</summary>
    public string Key => StepUpSeal.TargetKey(ActionPath, Binding);
}

/// <summary>
/// A step-up receipt — proof that <see cref="UserId"/> authenticated strongly and freshly FOR the
/// listed <see cref="Targets"/>. Minted only by the platform's step-up endpoint, written as System at
/// <c>Auth/_StepUp/{Id}</c>, sealed with a key derived from the instance master key, and consumed at
/// most once per target. See <c>Doc/Architecture/ApprovalStepUp</c>.
/// </summary>
public sealed record StepUpReceipt
{
    /// <summary>Random 128-bit id, hex.</summary>
    [Key]
    [Browsable(false)]
    public string Id { get; init; } = "";

    /// <summary>The approver's mesh user id (<c>AccessContext.ObjectId</c>) — never an email.</summary>
    public string UserId { get; init; } = "";

    /// <summary>How the step-up was performed — a <see cref="StepUpMethod"/> value.</summary>
    public string Method { get; init; } = "";

    /// <summary>The actions this receipt covers.</summary>
    public ImmutableList<StepUpTarget> Targets { get; init; } = [];

    /// <summary>When the receipt was minted.</summary>
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>After this instant the receipt is no longer consumable.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>When the user actually authenticated (Entra <c>auth_time</c>, the assertion time, the code time).</summary>
    public DateTimeOffset AuthenticatedAt { get; init; }

    /// <summary>What was verified, for the audit line — never a secret.</summary>
    public string? Evidence { get; init; }

    /// <summary>HMAC-SHA256 over every other field (<see cref="StepUpSeal"/>), base64.</summary>
    [Browsable(false)]
    public string Seal { get; init; } = "";
}

/// <summary>
/// The consumption marker of ONE target of ONE receipt, at <c>Auth/_StepUpUse/{receiptId}-{targetKey}</c>.
/// Its CREATION is the consumption: creating a node is atomic at the owning hub, so a second
/// consumer's create is refused by the store — a replay cannot be reset by editing a field.
/// </summary>
public sealed record StepUpConsumption
{
    /// <summary><c>{receiptId}-{targetKey}</c>.</summary>
    [Key]
    [Browsable(false)]
    public string Id { get; init; } = "";

    /// <summary>The receipt consumed.</summary>
    public string ReceiptId { get; init; } = "";

    /// <summary>The action path it was consumed for.</summary>
    public string ActionPath { get; init; } = "";

    /// <summary>The approver.</summary>
    public string UserId { get; init; } = "";

    /// <summary>When.</summary>
    public DateTimeOffset ConsumedAt { get; init; }

    /// <summary>
    /// A random value of the consuming call. The consumer reads the marker back after creating it
    /// and wins only when the STORED nonce is its own — a concurrent create response cannot say
    /// who won, the stored node can.
    /// </summary>
    [Browsable(false)]
    public string Nonce { get; init; } = "";
}

/// <summary>
/// A step-up in progress, held SERVER-side at <c>Auth/_StepUpPending/{Id}</c> between the moment the
/// approver is sent to authenticate and the moment the proof comes back — the browser carries only
/// the handle and the state (a bulk approval's targets would not fit a cookie). System-only;
/// deleted when the step-up completes; refused after <see cref="ExpiresAt"/>.
/// </summary>
public sealed record StepUpPending
{
    /// <summary>The handle (random, 128 bits, hex).</summary>
    [Key]
    [Browsable(false)]
    public string Id { get; init; } = "";

    /// <summary>The OAuth <c>state</c> / CSRF value the browser must present with the handle.</summary>
    [Browsable(false)]
    public string State { get; init; } = "";

    /// <summary>The nonce the proof must carry (the Entra <c>nonce</c>, the passkey challenge's salt).</summary>
    [Browsable(false)]
    public string Nonce { get; init; } = "";

    /// <summary>The approver.</summary>
    public string UserId { get; init; } = "";

    /// <summary>What is being stepped up for.</summary>
    public ImmutableList<StepUpTarget> Targets { get; init; } = [];

    /// <summary>Where to return.</summary>
    public string ReturnUrl { get; init; } = "/";

    /// <summary>After this the pending step-up is refused.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// The outcome of checking a step-up receipt — the <c>Outcome</c> of a <see cref="StepUpVerdict"/>.
/// Only <see cref="Accepted"/> and <see cref="NotRequired"/> let an approval proceed; every other
/// value PARKS it. Unknown values never count.
/// </summary>
public static class StepUpOutcome
{
    /// <summary>Step-up is not enabled on this instance.</summary>
    public const string NotRequired = "NotRequired";

    /// <summary>Valid, now consumed.</summary>
    public const string Accepted = "Accepted";

    /// <summary>No receipt is stamped for this approver, or the stamped one does not resolve.</summary>
    public const string Missing = "Missing";

    /// <summary>The receipt read did not answer — fail closed.</summary>
    public const string Unavailable = "Unavailable";

    /// <summary>The seal does not verify.</summary>
    public const string Invalid = "Invalid";

    /// <summary>The receipt belongs to another user.</summary>
    public const string WrongUser = "WrongUser";

    /// <summary>No target names this action path.</summary>
    public const string WrongAction = "WrongAction";

    /// <summary>The target's hash is not the hash being approved.</summary>
    public const string WrongBinding = "WrongBinding";

    /// <summary>Past its expiry.</summary>
    public const string Expired = "Expired";

    /// <summary>Already consumed for this target.</summary>
    public const string Replayed = "Replayed";

    /// <summary>True only for the two outcomes that let an approval proceed.</summary>
    public static bool Counts(string? outcome) => outcome is Accepted or NotRequired;
}

/// <summary>One step-up check's answer.</summary>
/// <param name="Outcome">A <see cref="StepUpOutcome"/> value.</param>
/// <param name="ReceiptId">The receipt examined, when there was one.</param>
/// <param name="Method">The receipt's method when accepted.</param>
/// <param name="Detail">An English diagnostic for logs; UI text comes from the localization catalog by outcome.</param>
public sealed record StepUpVerdict(string Outcome, string? ReceiptId = null, string? Method = null, string? Detail = null)
{
    /// <summary>True when the approval may proceed.</summary>
    public bool Counts => StepUpOutcome.Counts(Outcome);

    /// <summary>The localization key naming this outcome for a viewer (<c>stepUp.outcome.{Outcome}</c>).</summary>
    public string LocalizationKey => "stepUp.outcome." + Outcome;
}

/// <summary>
/// The step-up configuration of THIS instance, read from <c>Authentication:StepUp:*</c> — declared
/// per deployment record (<c>SignIn.StepUp</c>). Default OFF: absent, empty or unparseable
/// <see cref="EnabledKey"/> reads as disabled, so nothing changes until an instance declares it.
/// </summary>
public sealed record StepUpOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Authentication:StepUp";

    /// <summary>Whether every approval requires a receipt.</summary>
    public const string EnabledKey = Section + ":Enabled";

    /// <summary>The Entra Conditional Access authentication context id (<c>c1</c>…<c>c99</c>).</summary>
    public const string EntraContextKey = Section + ":Entra:AuthenticationContext";

    /// <summary>The tenant a step-up token must come from; defaults to the sign-in tenant.</summary>
    public const string EntraTenantKey = Section + ":Entra:TenantId";

    /// <summary>Require an <c>amr</c> claim naming a phishing-resistant method.</summary>
    public const string EntraRequireAmrKey = Section + ":Entra:RequireAmr";

    /// <summary>Comma-separated <c>amr</c> values that count as phishing-resistant.</summary>
    public const string EntraPhishingResistantAmrKey = Section + ":Entra:PhishingResistantAmr";

    /// <summary>How old the authentication may be when its proof arrives, in seconds.</summary>
    public const string MaxAuthAgeKey = Section + ":MaxAuthAgeSeconds";

    /// <summary>How long a receipt stays consumable, in seconds.</summary>
    public const string ReceiptLifetimeKey = Section + ":ReceiptLifetimeSeconds";

    /// <summary>Whether the TOTP rung exists.</summary>
    public const string AllowTotpFallbackKey = Section + ":AllowTotpFallback";

    /// <summary>
    /// The <c>amr</c> values counted as phishing-resistant unless configured otherwise: <c>fido</c>
    /// (FIDO2 security key, device-bound or synced passkey) and <c>hwk</c> (Windows Hello for
    /// Business, multi-factor certificate). NOT <c>ngcmfa</c> — Entra also emits it for an
    /// Authenticator PUSH, which is phishable — and NOT <c>x509</c>, which alone is not
    /// phishing-resistant MFA (Microsoft's own AMR table).
    /// </summary>
    public static readonly ImmutableHashSet<string> DefaultPhishingResistantAmr =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "fido", "hwk");

    /// <summary>Step-up is required on this instance.</summary>
    public bool Enabled { get; init; }

    /// <summary>The Entra authentication context id; null when not declared.</summary>
    public string? EntraAuthenticationContext { get; init; }

    /// <summary>The explicit step-up tenant; null ⇒ the sign-in tenant.</summary>
    public string? EntraTenantId { get; init; }

    /// <summary>
    /// An absent <c>amr</c> is a refusal — the DEFAULT, because <c>acrs</c> alone proves only that
    /// the context's policy was satisfied, and an authentication context with no Conditional Access
    /// policy behind it is issued to anybody. <c>amr</c> is a v2.0 optional ID-token claim the
    /// tenant admin adds to the app registration. Set <c>false</c> only to accept <c>acrs</c> alone.
    /// </summary>
    public bool EntraRequireAmr { get; init; } = true;

    /// <summary>The <c>amr</c> values that count as phishing-resistant.</summary>
    public ImmutableHashSet<string> EntraPhishingResistantAmr { get; init; } = DefaultPhishingResistantAmr;

    /// <summary>Maximum age of the authentication when its proof arrives.</summary>
    public TimeSpan MaxAuthAge { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How long a receipt stays consumable.</summary>
    public TimeSpan ReceiptLifetime { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>Whether the TOTP rung exists.</summary>
    public bool AllowTotpFallback { get; init; } = true;

    /// <summary>
    /// Reads the options LIVE from <paramref name="configuration"/> (configuration is layered and
    /// reloadable — read at each check, never cached at boot).
    /// </summary>
    /// <param name="configuration">The host configuration; null reads as all defaults (disabled).</param>
    /// <returns>The options.</returns>
    public static StepUpOptions From(IConfiguration? configuration)
    {
        if (configuration is null) return new StepUpOptions();
        var amr = configuration[EntraPhishingResistantAmrKey];
        return new StepUpOptions
        {
            Enabled = Bool(configuration[EnabledKey], false),
            EntraAuthenticationContext = Blank(configuration[EntraContextKey]),
            EntraTenantId = Blank(configuration[EntraTenantKey]),
            EntraRequireAmr = Bool(configuration[EntraRequireAmrKey], true),
            EntraPhishingResistantAmr = string.IsNullOrWhiteSpace(amr)
                ? DefaultPhishingResistantAmr
                : amr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            MaxAuthAge = Seconds(configuration[MaxAuthAgeKey], 120),
            ReceiptLifetime = Seconds(configuration[ReceiptLifetimeKey], 300),
            AllowTotpFallback = Bool(configuration[AllowTotpFallbackKey], true),
        };
    }

    private static bool Bool(string? raw, bool fallback) => bool.TryParse(raw, out var b) ? b : fallback;

    private static string? Blank(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static TimeSpan Seconds(string? raw, int fallback) =>
        TimeSpan.FromSeconds(int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : fallback);
}

/// <summary>
/// The receipt seal — HMAC-SHA256 over the receipt's canonical material, keyed with a key DERIVED
/// (HKDF-SHA256, purpose <see cref="Purpose"/>) from the instance master key, so the master key
/// itself never signs anything and a receipt from another instance never verifies here. Pure.
/// </summary>
public static class StepUpSeal
{
    /// <summary>The HKDF info string; changing it invalidates every outstanding receipt.</summary>
    public const string Purpose = "MeshWeaver.StepUp.Receipt.v2";

    /// <summary>Derives the seal key from the master key.</summary>
    /// <param name="masterKey">The instance master key.</param>
    /// <returns>32 key bytes.</returns>
    public static byte[] DeriveKey(byte[] masterKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt: null, info: Encoding.UTF8.GetBytes(Purpose));

    /// <summary>
    /// The canonical material of a receipt (every field but the seal): each field written as
    /// <c>{UTF-8 byte length}:{bytes}</c>, an absent value as <c>-</c>, instants as UTC ticks, the
    /// targets preceded by their count — so no two different receipts can produce the same bytes
    /// (a delimiter inside a path, an empty vs. absent evidence, a sub-millisecond change).
    /// </summary>
    /// <param name="r">The receipt.</param>
    /// <returns>The material.</returns>
    public static byte[] Material(StepUpReceipt r)
    {
        var sb = new StringBuilder();
        void Field(string? value)
        {
            if (value is null) { sb.Append('-'); return; }
            sb.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }
        Field(Purpose);
        Field(r.Id);
        Field(r.UserId);
        Field(r.Method);
        Field(r.IssuedAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Field(r.ExpiresAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Field(r.AuthenticatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Field(r.Evidence);
        Field(r.Targets.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var t in r.Targets)
        {
            Field(t.ActionPath);
            Field(t.Binding);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Computes the seal of <paramref name="receipt"/>.</summary>
    /// <param name="receipt">The receipt (its current <c>Seal</c> is ignored).</param>
    /// <param name="sealKey">The derived key (<see cref="DeriveKey"/>).</param>
    /// <returns>Base64 HMAC.</returns>
    public static string Compute(StepUpReceipt receipt, byte[] sealKey) =>
        Convert.ToBase64String(HMACSHA256.HashData(sealKey, Material(receipt)));

    /// <summary>Constant-time check of <paramref name="receipt"/>'s seal.</summary>
    /// <param name="receipt">The receipt.</param>
    /// <param name="sealKey">The derived key.</param>
    /// <returns>True when the seal matches.</returns>
    public static bool Verify(StepUpReceipt receipt, byte[] sealKey)
    {
        if (string.IsNullOrEmpty(receipt.Seal)) return false;
        byte[] given;
        try { given = Convert.FromBase64String(receipt.Seal); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(sealKey, Material(receipt));
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>The short stable key of a target: 16 hex chars of SHA-256(path \u001f binding).</summary>
    /// <param name="actionPath">The target path.</param>
    /// <param name="binding">The target hash.</param>
    /// <returns>The key.</returns>
    public static string TargetKey(string actionPath, string binding) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actionPath + "\u001f" + binding)))[..16].ToLowerInvariant();

    /// <summary>A fresh random receipt id (128 bits, lowercase hex).</summary>
    /// <returns>The id.</returns>
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

/// <summary>
/// Where step-up nodes live and how a receipt id reaches the approval it covers.
/// </summary>
public static class StepUpPaths
{
    /// <summary>NodeType of a receipt.</summary>
    public const string ReceiptNodeType = "StepUpReceipt";

    /// <summary>NodeType of a consumption marker.</summary>
    public const string ConsumptionNodeType = "StepUpConsumption";

    /// <summary>Namespace of receipts.</summary>
    public const string ReceiptNamespace = "Auth/_StepUp";

    /// <summary>Namespace of consumption markers.</summary>
    public const string ConsumptionNamespace = "Auth/_StepUpUse";

    /// <summary>NodeType of a step-up in progress.</summary>
    public const string PendingNodeType = "StepUpPending";

    /// <summary>Namespace of pending step-ups.</summary>
    public const string PendingNamespace = "Auth/_StepUpPending";

    /// <summary>Path of a pending step-up.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>The node path.</returns>
    public static string Pending(string handle) => PendingNamespace + "/" + handle;

    /// <summary>
    /// The content property a step-up endpoint stamps on each target node: a map
    /// <c>{ userId → receiptId }</c>, so several signers of one item each carry their own. A
    /// consumer's content record declares it as <c>ImmutableDictionary&lt;string,string&gt;? StepUpReceipts</c>.
    /// </summary>
    public const string StampProperty = "stepUpReceipts";

    /// <summary>The portal route that starts a step-up (an MVC endpoint — navigate with a full page load).</summary>
    public const string StartRoute = "/auth/step-up";

    /// <summary>
    /// The URL a page sends the approver to: <c>/auth/step-up?target=…&amp;binding=…[…]&amp;returnUrl=…</c>.
    /// One step-up may cover several targets (a bulk approval).
    /// </summary>
    /// <param name="targets">The actions to step up for.</param>
    /// <param name="returnUrl">A local URL to come back to.</param>
    /// <returns>The relative URL.</returns>
    public static string StartUrl(IEnumerable<StepUpTarget> targets, string? returnUrl)
    {
        var sb = new StringBuilder(StartRoute);
        var sep = '?';
        foreach (var t in targets)
        {
            sb.Append(sep).Append("target=").Append(Uri.EscapeDataString(t.ActionPath))
                .Append("&binding=").Append(Uri.EscapeDataString(t.Binding));
            sep = '&';
        }
        if (!string.IsNullOrEmpty(returnUrl))
            sb.Append(sep).Append("returnUrl=").Append(Uri.EscapeDataString(returnUrl));
        return sb.ToString();
    }

    /// <summary>Path of a receipt.</summary>
    /// <param name="receiptId">The receipt id.</param>
    /// <returns>The node path.</returns>
    public static string Receipt(string receiptId) => ReceiptNamespace + "/" + receiptId;

    /// <summary>Path of a consumption marker.</summary>
    /// <param name="receiptId">The receipt id.</param>
    /// <param name="targetKey">The target key.</param>
    /// <returns>The node path.</returns>
    public static string Consumption(string receiptId, string targetKey) => ConsumptionNamespace + "/" + receiptId + "-" + targetKey;

    /// <summary>True when <paramref name="receiptId"/> is shaped like a receipt id (32 lowercase hex) — anything else is never read.</summary>
    /// <param name="receiptId">The candidate.</param>
    /// <returns>True for a well-formed id.</returns>
    public static bool IsWellFormedId(string? receiptId) =>
        receiptId is { Length: 32 } && receiptId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>The receipt id stamped for <paramref name="userId"/> in a consumer's map, or null.</summary>
    /// <param name="stamps">The consumer's <c>StepUpReceipts</c> map.</param>
    /// <param name="userId">The approver.</param>
    /// <returns>The receipt id, or null.</returns>
    public static string? ReceiptFor(IReadOnlyDictionary<string, string>? stamps, string? userId) =>
        stamps is null || string.IsNullOrEmpty(userId) ? null
            : stamps.TryGetValue(userId, out var id) ? id : null;

    /// <summary>
    /// Returns <paramref name="content"/> with <c>stepUpReceipts[userId] = receiptId</c> set — an
    /// ordinary content transform for <c>GetMeshNodeStream(path).Update(...)</c>. A typed content
    /// keeps its CLR type (round-tripped through JSON); a type that does not declare the property
    /// drops it, which leaves the approval without a receipt — fail closed, never a pass.
    /// </summary>
    /// <param name="content">The node's current content.</param>
    /// <param name="userId">The approver.</param>
    /// <param name="receiptId">The receipt.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <returns>The stamped content.</returns>
    public static object? Stamp(object? content, string userId, string receiptId, JsonSerializerOptions options)
    {
        if (content is null) return null;
        var json = content is JsonElement el
            ? JsonNode.Parse(el.GetRawText()) as JsonObject
            : JsonSerializer.SerializeToNode(content, content.GetType(), options) as JsonObject;
        if (json is null) return content;
        // The property as the serializer writes it: an existing key in either casing wins, else
        // the naming policy's spelling — a case-sensitive deserializer would drop any other.
        var key = json.ContainsKey("StepUpReceipts") ? "StepUpReceipts"
            : json.ContainsKey(StampProperty) ? StampProperty
            : options.PropertyNamingPolicy?.ConvertName("StepUpReceipts") ?? "StepUpReceipts";
        var map = json[key] as JsonObject ?? new JsonObject();
        map[userId] = receiptId;
        json[key] = map;
        return content is JsonElement
            ? JsonSerializer.Deserialize<JsonElement>(json.ToJsonString())
            : json.Deserialize(content.GetType(), options) ?? content;
    }
}

/// <summary>
/// The CONSUMER contract of the approval step-up: check and consume a receipt. Registered by the
/// mesh; resolve with <c>hub.ServiceProvider.GetRequiredService&lt;IStepUpService&gt;()</c>. Reads
/// and writes the receipt nodes as System internally — a consumer compiled in the mesh needs no
/// impersonation.
///
/// <para>🚨 There is deliberately NO way to MINT a receipt through this interface: a receipt is
/// proof that a step-up endpoint authenticated the approver just now, so issuing one is internal to
/// the platform (the step-up endpoints in the portal host). Anything that could resolve a public
/// mint could stamp itself a valid receipt and skip the authentication it stands for.</para>
/// </summary>
public interface IStepUpService
{
    /// <summary>The instance's step-up options, read live.</summary>
    StepUpOptions Options { get; }

    /// <summary>
    /// Checks the receipt stamped for an approval and, when valid, consumes it for this target.
    /// Cold, single emission, never faults — every failure is an outcome. When step-up is disabled
    /// it answers <see cref="StepUpOutcome.NotRequired"/> without reading anything.
    /// </summary>
    /// <param name="receiptId">The receipt id stamped for the approver (<see cref="StepUpPaths.ReceiptFor"/>); null ⇒ Missing.</param>
    /// <param name="approver">The approver's mesh id.</param>
    /// <param name="actionPath">The approval target's node path.</param>
    /// <param name="binding">The hash being approved.</param>
    /// <returns>The verdict.</returns>
    IObservable<StepUpVerdict> Consume(string? receiptId, string approver, string actionPath, string binding);

    /// <summary>
    /// Checks the receipt WITHOUT consuming it — for a consumer that must decide before its last
    /// gate. Same outcomes as <see cref="Consume"/> except <see cref="StepUpOutcome.Replayed"/>,
    /// which only the consuming create can establish.
    /// </summary>
    /// <param name="receiptId">The receipt id stamped for the approver.</param>
    /// <param name="approver">The approver's mesh id.</param>
    /// <param name="actionPath">The approval target's node path.</param>
    /// <param name="binding">The hash being approved.</param>
    /// <returns>The verdict.</returns>
    IObservable<StepUpVerdict> Check(string? receiptId, string approver, string actionPath, string binding);
}
