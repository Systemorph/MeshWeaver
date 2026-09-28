using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// One secret that a global administrator set THROUGH THE PORTAL for a configuration key. It is
/// stored on the node <c>Admin/Secret-{key}</c>, encrypted at rest.
///
/// <para>🚨 Nothing on this record is ever shown or returned in the clear. The value fields hold
/// <c>enc:</c>-tagged ciphertext from <see cref="IProviderKeyProtector"/>, and a UI reads only
/// <see cref="SecretStatus"/>. Only <see cref="InstanceSecretCatalog"/> decrypts, and it hands
/// values only to the code that signs or verifies with them.</para>
/// </summary>
public record InstanceSecretContent
{
    /// <summary>The configuration key this secret supplies, e.g. <c>Hosting:ControlInbox:Secret</c>.</summary>
    public string ConfigKey { get; init; } = "";

    /// <summary>The current value, <c>enc:</c>-tagged.</summary>
    [Browsable(false)]
    public string? EncryptedValue { get; init; }

    /// <summary>The <see cref="SecretFingerprint"/> of the current value.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>How the current value was produced (<see cref="SecretSources"/>).</summary>
    public string? Source { get; init; }

    /// <summary>When the secret was first created.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When the current value was set.</summary>
    public DateTimeOffset? SetAt { get; init; }

    /// <summary>Who set the current value (a user id; never a value).</summary>
    public string? SetBy { get; init; }

    /// <summary>When the value or the state last changed, and <see cref="UpdatedBy"/> who changed it.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Who last changed the value or the state.</summary>
    public string? UpdatedBy { get; init; }

    /// <summary>
    /// True when a global administrator DISABLED the secret. A disabled entry keeps its value but
    /// is a tombstone: it also suppresses any value the same key has in the deployment
    /// configuration, so disabling in the portal really stops the key working, and does not fall
    /// back to a mounted copy. <see cref="InstanceSecrets.Enable"/> reverses it.
    /// </summary>
    public bool Disabled { get; init; }

    /// <summary>
    /// When the secret was DELETED. A deleted entry is ignored, so the deployment configuration's
    /// value applies again. The ciphertext is kept until <see cref="RecoverableUntil"/> so
    /// <see cref="InstanceSecrets.Recover"/> can undo the deletion.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>Until when a deleted secret can be recovered.</summary>
    public DateTimeOffset? RecoverableUntil { get; init; }

    /// <summary>
    /// The value the current one replaced, <c>enc:</c>-tagged, kept during a ROTATION so both
    /// verify until the other end has the new one. It is cleared when a delivery first verifies
    /// with the current value, or ignored once <see cref="PreviousUntil"/> passes.
    /// </summary>
    [Browsable(false)]
    public string? PreviousEncryptedValue { get; init; }

    /// <summary>The fingerprint of <see cref="PreviousEncryptedValue"/>.</summary>
    public string? PreviousFingerprint { get; init; }

    /// <summary>When the previous value stops verifying, even if the new one was never used.</summary>
    public DateTimeOffset? PreviousUntil { get; init; }

    /// <summary>When the secret was last USED (a signature made or verified with it).</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Whether that use succeeded, for example whether the other end accepted the signature.</summary>
    public bool? LastUseOk { get; init; }

    /// <summary>One sentence about that use, e.g. "accepted by the control instance as 'fabrikam'" —
    /// keyed, so it renders in the viewer's language. Never a value.</summary>
    public LocalizableText? LastUseResult { get; init; }
}

/// <summary>
/// What a UI may know about a configuration key's secret on this installation: the standard
/// <see cref="SecretStatus"/>, where the effective value comes from, a pending rotation, and the
/// last use. It never includes a value.
/// </summary>
/// <param name="Secret">The standard status.</param>
/// <param name="Origin">Where the effective value comes from: <see cref="FromPortal"/>,
/// <see cref="FromConfiguration"/> or <see cref="FromNone"/>.</param>
/// <param name="PreviousFingerprint">The fingerprint of the value still accepted during a rotation.</param>
/// <param name="PreviousUntil">When that previous value stops being accepted.</param>
/// <param name="LastUsedAt">When the secret was last used.</param>
/// <param name="LastUseOk">Whether that use succeeded.</param>
/// <param name="LastUseResult">One sentence about that use.</param>
public sealed record InstanceSecretStatus(
    SecretStatus Secret,
    string Origin,
    string? PreviousFingerprint = null,
    DateTimeOffset? PreviousUntil = null,
    DateTimeOffset? LastUsedAt = null,
    bool? LastUseOk = null,
    LocalizableText? LastUseResult = null)
{
    /// <summary>The value was set in the portal.</summary>
    public const string FromPortal = "portal";

    /// <summary>The value comes from the deployment configuration (e.g. a vault mount).</summary>
    public const string FromConfiguration = "configuration";

    /// <summary>There is no value.</summary>
    public const string FromNone = "none";
}

/// <summary>
/// Why a secret operation wrote nothing — a keyed sentence, so the portal shows it in the viewer's
/// language (<see cref="Text"/>). <see cref="Exception.Message"/> is the English rendering. It never
/// contains a secret value.
/// </summary>
/// <param name="text">The refusal.</param>
public sealed class InstanceSecretException(LocalizableText text) : InvalidOperationException(text.English)
{
    /// <summary>The refusal, keyed for the viewer's language.</summary>
    public LocalizableText Text { get; } = text;

    internal static InstanceSecretException Of(string english, string key, params (string Name, object? Value)[] args) =>
        new(LocalizableText.Keyed(english, key, args));
}

/// <summary>
/// A configuration key that may be set through the portal. Registered by the code that READS the
/// key (<see cref="InstanceSecrets.AddInstanceSecretSlot{TBuilder}"/>). A key is settable only if
/// some registered slot admits it, so the portal can never override an arbitrary setting.
/// </summary>
/// <param name="Pattern">An exact key, or a section followed by <c>:*</c>, which admits exactly one
/// more segment of letters, digits, <c>-</c>, <c>_</c> or <c>.</c>. The section key itself is NOT admitted.</param>
public sealed record InstanceSecretSlot(string Pattern);

/// <summary>
/// Secrets a global administrator ENTERS in the portal, instead of an operator minting them in a
/// vault. They are stored encrypted in the mesh, and code that signs or verifies picks them up
/// live, without a restart. See <c>Doc/Architecture/InstanceSecrets</c>.
///
/// <para>Contract:</para>
/// <list type="bullet">
/// <item><b>Write-only.</b> Every verb returns an <see cref="InstanceSecretStatus"/>, never a value.
/// The only exception is <see cref="Generate"/>, which returns the value it minted ONCE, so it can
/// be shown to the person who asked for it. Only the signing or verifying code reads a stored value
/// (<see cref="Resolve"/>, <see cref="Candidates"/>).</item>
/// <item><b>Rights are checked first, and the write runs as system.</b> The caller must be a global
/// administrator, which is checked against the caller's own identity before anything is written.
/// The node is then written as system, so the Admin partition's creatable-type curation never
/// decides. SetBy records the caller.</item>
/// <item><b>Encrypted or refused.</b> A value that does not come back from the protector
/// <c>enc:</c>-tagged is refused, never stored in the clear.</item>
/// <item><b>Only registered slots.</b> A key that no <see cref="InstanceSecretSlot"/> admits is refused.</item>
/// </list>
/// </summary>
public static class InstanceSecrets
{
    /// <summary>The node type of a stored secret.</summary>
    public const string NodeType = "InstanceSecret";

    /// <summary>The partition the secrets live in.</summary>
    public const string Partition = "Admin";

    /// <summary>The id prefix of a secret's node.</summary>
    public const string IdPrefix = "Secret-";

    /// <summary>The configuration SUB-key under which a rotation's previous value is offered to a verifier.</summary>
    public const string PreviousSubKey = "Previous";

    /// <summary>How long a rotation's previous value stays valid by default.</summary>
    public static readonly TimeSpan DefaultRotationOverlap = TimeSpan.FromDays(14);

    /// <summary>How long a deleted secret can be recovered.</summary>
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromDays(7);

    /// <summary>The query the catalog keeps live.</summary>
    public const string CatalogQuery = $"namespace:{Partition} nodeType:{NodeType}";

    private static readonly Regex SegmentShape = new("^[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

    /// <summary>Registers the node type, the catalog, and the content type on every hub.</summary>
    public static TBuilder AddInstanceSecretType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureHub(config => config.WithType<InstanceSecretContent>(nameof(InstanceSecretContent)));
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureServices(s => s.AddSingleton<InstanceSecretCatalog>());
        return builder;
    }

    /// <summary>
    /// Declares that <paramref name="pattern"/> may be set through the portal
    /// (see <see cref="InstanceSecretSlot"/>). Call it where the key is READ.
    /// </summary>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="pattern">An exact configuration key, or <c>{section}:*</c>.</param>
    public static TBuilder AddInstanceSecretSlot<TBuilder>(this TBuilder builder, string pattern) where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(s => s.AddSingleton(new InstanceSecretSlot(pattern)));
        return builder;
    }

    /// <summary>The node type definition.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Instance Secret",
        Icon = "/static/NodeTypeIcons/key.svg",
        // Never offered for creation, never indexed as context: the content is ciphertext and metadata.
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<InstanceSecretContent>())
    };

    /// <summary>
    /// The node id for <paramref name="configKey"/>: <c>Secret-</c> plus the key LOWER-CASED with
    /// <c>:</c> as <c>--</c>. Configuration keys compare case-insensitively, so two spellings of one
    /// key must address one node. The encoding is injective because <see cref="Admits"/> refuses any
    /// key containing <c>--</c>. Pure.
    /// </summary>
    public static string IdOf(string configKey) =>
        IdPrefix + configKey.Trim().ToLowerInvariant().Replace(":", "--");

    /// <summary>The node path for <paramref name="configKey"/>. Pure.</summary>
    public static string PathOf(string configKey) => $"{Partition}/{IdOf(configKey)}";

    /// <summary>Whether <paramref name="pattern"/> admits <paramref name="configKey"/>. Pure.</summary>
    public static bool Admits(string pattern, string configKey)
    {
        var key = (configKey ?? "").Trim();
        var p = (pattern ?? "").Trim();
        // `--` is the node id's separator (IdOf): a key containing it could collide with another.
        if (key.Length == 0 || p.Length == 0 || key.Contains("--", StringComparison.Ordinal))
            return false;
        if (!p.EndsWith(":*", StringComparison.Ordinal))
            return string.Equals(p, key, StringComparison.OrdinalIgnoreCase);
        var section = p[..^2];
        if (!key.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = key[(section.Length + 1)..];
        return rest.Length > 0 && !rest.Contains(':') && SegmentShape.IsMatch(rest);
    }

    /// <summary>Whether any registered slot admits <paramref name="configKey"/>.</summary>
    public static bool IsSettable(IMessageHub hub, string configKey) =>
        hub.ServiceProvider.GetServices<InstanceSecretSlot>().Any(s => Admits(s.Pattern, configKey));

    /// <summary>
    /// The effective value of <paramref name="configKey"/> for SIGNING: the value set in the portal
    /// if there is one; nothing if it was disabled in the portal; otherwise the deployment
    /// configuration's value (also when the portal's entry was deleted). Never logged; never shown.
    /// </summary>
    public static string? Resolve(IMessageHub hub, string configKey)
    {
        var catalog = hub.ServiceProvider.GetService<InstanceSecretCatalog>();
        if (catalog?.Entry(configKey) is { IsDeleted: false } entry)
            return entry.IsDisabled ? null : entry.Current;
        var value = hub.ServiceProvider.GetService<IConfiguration>()?[configKey];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Every per-sender key under <paramref name="section"/> that may VERIFY a delivery: for each
    /// child, the portal's current value and its rotation's previous value when the portal holds the
    /// child, else the configuration's value (and its <c>:Previous</c> sub-key). A child disabled in
    /// the portal contributes nothing; a child deleted in the portal falls back to configuration.
    /// Per child, the current value comes first. Pure over the catalog and the configuration.
    /// </summary>
    public static ImmutableList<(string Child, ImmutableList<string> Values)> Candidates(
        InstanceSecretCatalog? catalog, IConfiguration? configuration, string section)
    {
        // A faulted catalog cannot see a revocation any more: offer no per-sender key at all
        // (fail closed), rather than a mounted key the portal may have disabled since.
        if (catalog?.Faulted == true)
            return ImmutableList<(string, ImmutableList<string>)>.Empty;
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableList<string>>(StringComparer.OrdinalIgnoreCase);
        if (configuration is not null)
            foreach (var child in configuration.GetSection(section).GetChildren())
            {
                var values = ImmutableList.CreateBuilder<string>();
                if (!string.IsNullOrWhiteSpace(child.Value))
                    values.Add(child.Value!);
                if (child[PreviousSubKey] is { } previous && !string.IsNullOrWhiteSpace(previous))
                    values.Add(previous);
                if (values.Count > 0)
                    result[child.Key] = values.ToImmutable();
            }
        if (catalog is not null)
            foreach (var (child, entry) in catalog.ChildrenOf(section))
            {
                if (entry.IsDeleted)
                    continue;
                if (entry.IsDisabled || entry.Current is null)
                {
                    result.Remove(child);
                    continue;
                }
                result[child] = entry.Previous is { } previous
                    ? ImmutableList.Create(entry.Current, previous)
                    : ImmutableList.Create(entry.Current);
            }
        return result.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key, StringComparer.Ordinal).ToImmutableList();
    }

    /// <summary>The status of <paramref name="configKey"/> as a UI may show it. Never includes a value.</summary>
    public static InstanceSecretStatus StatusOf(IMessageHub hub, string configKey)
    {
        var catalog = hub.ServiceProvider.GetService<InstanceSecretCatalog>();
        var configured = hub.ServiceProvider.GetService<IConfiguration>()?[configKey];
        return StatusOf(configKey, catalog?.Entry(configKey), configured);
    }

    /// <summary>The status, from a catalog entry and a configured value. Pure.</summary>
    public static InstanceSecretStatus StatusOf(string configKey, InstanceSecretCatalog.CatalogEntry? entry, string? configured)
    {
        var configuredStatus = string.IsNullOrWhiteSpace(configured)
            ? null
            : new SecretStatus { Name = configKey, Present = true, Enabled = true, Fingerprint = SecretFingerprint.Of(configured) };
        if (entry is null)
            return configuredStatus is null
                ? new(new SecretStatus { Name = configKey }, InstanceSecretStatus.FromNone)
                : new(configuredStatus, InstanceSecretStatus.FromConfiguration);

        var c = entry.Content;
        var portal = new SecretStatus
        {
            Name = configKey,
            Present = c.EncryptedValue is not null,
            // As kv_status reports it: true/false for a live value, null when absent or deleted —
            // except that a DISABLED entry is always false, including a tombstone over a mounted key.
            Enabled = c.Disabled ? false : c.EncryptedValue is not null && c.DeletedAt is null ? true : null,
            Created = c.CreatedAt,
            Updated = c.UpdatedAt,
            Deleted = c.DeletedAt is not null,
            RecoverableUntil = c.DeletedAt is null ? null : c.RecoverableUntil,
            Fingerprint = c.Fingerprint,
            SetBy = c.SetBy,
            SetAt = c.SetAt,
            Source = c.Source,
        };
        // A deleted portal entry gives way to the configuration — but it is still SHOWN, so it can be recovered.
        if (entry.IsDeleted && configuredStatus is not null)
            return new(configuredStatus with { Deleted = false }, InstanceSecretStatus.FromConfiguration,
                LastUsedAt: c.LastUsedAt, LastUseOk: c.LastUseOk, LastUseResult: c.LastUseResult);
        return new(portal, entry.IsDeleted ? InstanceSecretStatus.FromNone : InstanceSecretStatus.FromPortal,
            entry.Previous is null ? null : c.PreviousFingerprint,
            entry.Previous is null ? null : c.PreviousUntil,
            c.LastUsedAt, c.LastUseOk, c.LastUseResult);
    }

    /// <summary>The live status of <paramref name="configKey"/>, re-emitted whenever the catalog changes.</summary>
    public static IObservable<InstanceSecretStatus> ObserveStatus(IMessageHub hub, string configKey)
    {
        var catalog = hub.ServiceProvider.GetService<InstanceSecretCatalog>();
        if (catalog is null)
            return Observable.Return(StatusOf(hub, configKey));
        return catalog.Changes.Select(_ => StatusOf(hub, configKey)).DistinctUntilChanged();
    }

    /// <summary>
    /// Sets <paramref name="configKey"/> to <paramref name="value"/>, as pasted by a person. Cold:
    /// nothing happens until it is subscribed. Emits the resulting status, or fails with an
    /// <see cref="InvalidOperationException"/> whose message says why nothing was written. The
    /// message never contains the value.
    /// </summary>
    /// <param name="hub">The caller's hub. The caller's identity is read at SUBSCRIBE time.</param>
    /// <param name="configKey">A key some registered <see cref="InstanceSecretSlot"/> admits.</param>
    /// <param name="value">The value, used byte for byte.</param>
    /// <param name="keepPreviousFor">During a ROTATION, how long the value being replaced keeps
    /// verifying — at most <see cref="DefaultRotationOverlap"/>; a longer span is clamped to it, so no
    /// caller can keep a replaced credential alive indefinitely. Null replaces it outright.</param>
    /// <param name="source">How the value was produced (<see cref="SecretSources"/>).</param>
    public static IObservable<InstanceSecretStatus> Set(
        IMessageHub hub, string configKey, string value, TimeSpan? keepPreviousFor = null,
        string source = SecretSources.Paste) =>
        Write(hub, configKey, mustExist: false, (current, user, now, protect) =>
        {
            if (string.IsNullOrEmpty(value))
                throw InstanceSecretException.Of("The value is empty, so nothing was saved.", "secret.error.empty");
            // 🚨 The protector treats an `enc:`-tagged input as ALREADY encrypted and returns it
            // unchanged, so a pasted `enc:…` would be stored verbatim — in the clear.
            if (value.StartsWith("enc:", StringComparison.Ordinal))
                throw InstanceSecretException.Of(
                    "A value may not start with 'enc:' (that prefix marks encrypted data), so nothing was saved.",
                    "secret.error.reservedPrefix");
            var encrypted = Protect(protect, value);
            var overlap = keepPreviousFor is { } requested && requested > TimeSpan.Zero
                ? (requested > DefaultRotationOverlap ? DefaultRotationOverlap : requested)
                : (TimeSpan?)null;
            var keepPrevious = overlap is not null
                && current is { Disabled: false, DeletedAt: null, EncryptedValue: { Length: > 0 } };
            return (current ?? new InstanceSecretContent { CreatedAt = now }) with
            {
                ConfigKey = configKey.Trim(),
                EncryptedValue = encrypted,
                Fingerprint = SecretFingerprint.Of(value),
                Source = source,
                SetAt = now,
                SetBy = user,
                UpdatedAt = now,
                UpdatedBy = user,
                Disabled = false,
                DeletedAt = null,
                RecoverableUntil = null,
                PreviousEncryptedValue = keepPrevious ? current!.EncryptedValue : null,
                PreviousFingerprint = keepPrevious ? current!.Fingerprint : null,
                PreviousUntil = keepPrevious ? now + overlap!.Value : null,
                LastUsedAt = null,
                LastUseOk = null,
                LastUseResult = null,
            };
        });

    /// <summary>A value minted by <see cref="Generate"/>, returned ONCE so it can be shown to the person who asked for it.</summary>
    /// <param name="Value">The new value. Show it once; never store or log it.</param>
    /// <param name="Status">The resulting status.</param>
    public sealed record Generated(string Value, InstanceSecretStatus Status);

    /// <summary>
    /// Generates a strong key (<see cref="SecretFingerprint.Generate"/>) on the server, stores it
    /// like <see cref="Set"/>, and returns it ONCE. Cold. The caller shows the value to the person
    /// who asked, with a warning that it will not be shown again, and then discards it.
    /// </summary>
    public static IObservable<Generated> Generate(IMessageHub hub, string configKey, TimeSpan? keepPreviousFor = null) =>
        Observable.Defer(() =>
        {
            var value = SecretFingerprint.Generate();
            return Set(hub, configKey, value, keepPreviousFor, SecretSources.Generate)
                .Select(status => new Generated(value, status));
        });

    /// <summary>
    /// Disables <paramref name="configKey"/>: the portal's value AND any configured value stop
    /// working at once (see <see cref="InstanceSecretContent.Disabled"/>). The value is kept, so
    /// <see cref="Enable"/> reverses it. Works on a key the portal does not hold yet, which is how a
    /// MOUNTED key is revoked from the portal. Same checks as <see cref="Set"/>.
    /// </summary>
    public static IObservable<InstanceSecretStatus> Disable(IMessageHub hub, string configKey) =>
        Write(hub, configKey, mustExist: false, (current, user, now, _) => (current ?? new InstanceSecretContent { CreatedAt = now }) with
        {
            ConfigKey = configKey.Trim(),
            Disabled = true,
            PreviousEncryptedValue = null,
            PreviousFingerprint = null,
            PreviousUntil = null,
            UpdatedAt = now,
            UpdatedBy = user,
        });

    /// <summary>Re-enables a disabled secret. Same checks as <see cref="Set"/>.</summary>
    public static IObservable<InstanceSecretStatus> Enable(IMessageHub hub, string configKey) =>
        Write(hub, configKey, mustExist: true, (current, user, now, _) => current! with
        {
            Disabled = false,
            UpdatedAt = now,
            UpdatedBy = user,
        });

    /// <summary>
    /// Deletes the portal's value of <paramref name="configKey"/>: it stops being used, and the
    /// deployment configuration's value (if any) applies again. Recoverable for
    /// <see cref="RecoveryWindow"/> through <see cref="Recover"/>. A UI asks for confirmation first.
    /// Same checks as <see cref="Set"/>.
    /// </summary>
    public static IObservable<InstanceSecretStatus> Delete(IMessageHub hub, string configKey) =>
        Write(hub, configKey, mustExist: true, (current, user, now, _) => current! with
        {
            DeletedAt = now,
            RecoverableUntil = now + RecoveryWindow,
            PreviousEncryptedValue = null,
            PreviousFingerprint = null,
            PreviousUntil = null,
            UpdatedAt = now,
            UpdatedBy = user,
        });

    /// <summary>Undoes a <see cref="Delete"/> within its recovery window. Same checks as <see cref="Set"/>.</summary>
    public static IObservable<InstanceSecretStatus> Recover(IMessageHub hub, string configKey) =>
        Write(hub, configKey, mustExist: true, (current, user, now, _) =>
        {
            if (current!.DeletedAt is null)
                throw InstanceSecretException.Of("The secret is not deleted, so there is nothing to recover.", "secret.error.notDeleted");
            if (current.RecoverableUntil is { } until && until <= now)
                throw InstanceSecretException.Of("The recovery window has passed. Set a new value instead.", "secret.error.recoveryPassed");
            return current with
            {
                DeletedAt = null,
                RecoverableUntil = null,
                UpdatedAt = now,
                UpdatedBy = user,
            };
        });

    /// <summary>
    /// Records one USE of a portal-set secret: whether it succeeded and one sentence about it.
    /// Written as system, because the signing or verifying code runs as system. When
    /// <paramref name="verifiedWithCurrent"/> is true, a pending rotation is complete and the
    /// previous value is dropped. A key the portal does not hold is not recorded. Never throws;
    /// a failed write is logged.
    /// </summary>
    public static IObservable<Unit> RecordUse(
        IMessageHub hub, string configKey, bool ok, LocalizableText result, bool verifiedWithCurrent = false)
    {
        var catalog = hub.ServiceProvider.GetService<InstanceSecretCatalog>();
        if (catalog?.Entry(configKey) is not { IsDeleted: false, IsDisabled: false })
            return Observable.Return(Unit.Default);
        var access = hub.ServiceProvider.GetService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstanceSecrets));
        var now = DateTimeOffset.UtcNow;
        return access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream(PathOf(configKey))
                .Update<InstanceSecretContent>((node, current) =>
                {
                    var c = current ?? new InstanceSecretContent { ConfigKey = configKey };
                    var completeRotation = verifiedWithCurrent && c.PreviousEncryptedValue is not null;
                    return node with
                    {
                        Content = c with
                        {
                            LastUsedAt = now,
                            LastUseOk = ok,
                            LastUseResult = result,
                            PreviousEncryptedValue = completeRotation ? null : c.PreviousEncryptedValue,
                            PreviousFingerprint = completeRotation ? null : c.PreviousFingerprint,
                            PreviousUntil = completeRotation ? null : c.PreviousUntil,
                        },
                    };
                })
                .Take(1)
                .Select(_ => Unit.Default))
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogWarning(ex, "[InstanceSecrets] could not record a use of {ConfigKey}", configKey);
                return Observable.Return(Unit.Default);
            });
    }

    private static string Protect(IProviderKeyProtector? protector, string value)
    {
        if (protector is null)
            throw InstanceSecretException.Of(
                "This installation registers no key protector, so the secret cannot be stored encrypted. Nothing was saved.",
                "secret.error.noProtector");
        string? stored;
        try
        {
            stored = protector.Protect(value);
        }
        catch (Exception ex)
        {
            // The protector's message names the missing master key, never the value.
            throw InstanceSecretException.Of(
                $"The secret could not be encrypted ({ex.Message}). Nothing was saved.",
                "secret.error.encryptFailed", ("reason", ex.Message));
        }
        if (stored is null || !stored.StartsWith("enc:", StringComparison.Ordinal))
            throw InstanceSecretException.Of(
                "Encryption is unavailable on this installation (no master key), so the secret was refused rather than stored in cleartext.",
                "secret.error.noMasterKey");
        return stored;
    }

    private delegate InstanceSecretContent Fold(
        InstanceSecretContent? current, string user, DateTimeOffset now, IProviderKeyProtector? protector);

    /// <summary>
    /// The one write path. Rights are checked first as the CALLER, and the write then runs as system.
    /// Create when the catalog does not know the node; update otherwise, or when a concurrent create won.
    /// </summary>
    private static IObservable<InstanceSecretStatus> Write(IMessageHub hub, string configKey, bool mustExist, Fold fold) =>
        Observable.Defer(() =>
        {
            var key = (configKey ?? "").Trim();
            if (!IsSettable(hub, key))
                return Observable.Throw<InstanceSecretStatus>(InstanceSecretException.Of(
                    $"'{key}' is not a setting this installation lets the portal set.",
                    "secret.error.notSettable", ("key", key)));
            var access = hub.ServiceProvider.GetService<AccessService>();
            var user = (access?.Context ?? access?.CircuitContext)?.ObjectId;
            if (string.IsNullOrEmpty(user))
                return Observable.Throw<InstanceSecretStatus>(InstanceSecretException.Of(
                    "No signed-in user, so nothing was saved.", "secret.error.noUser"));
            var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
            var catalog = hub.ServiceProvider.GetRequiredService<InstanceSecretCatalog>();
            var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var path = PathOf(key);

            IObservable<InstanceSecretContent> Update() =>
                hub.GetWorkspace().GetMeshNodeStream(path)
                    .Update<InstanceSecretContent>((node, current) =>
                        node with { Content = fold(current, user, DateTimeOffset.UtcNow, protector) })
                    .Take(1)
                    .Select(n => n.ContentAs<InstanceSecretContent>(hub.JsonSerializerOptions) ?? new InstanceSecretContent());

            IObservable<InstanceSecretContent> Create()
            {
                InstanceSecretContent content;
                try
                {
                    content = fold(null, user, DateTimeOffset.UtcNow, protector);
                }
                catch (Exception ex)
                {
                    return Observable.Throw<InstanceSecretContent>(ex);
                }
                var node = new MeshNode(IdOf(key), Partition)
                {
                    NodeType = NodeType,
                    Name = key,
                    State = MeshNodeState.Active,
                    Content = content,
                };
                return mesh.CreateNode(node).Take(1).Select(_ => content)
                    .Catch<InstanceSecretContent, Exception>(ex => IsAlreadyExists(ex)
                        ? Update()
                        : Observable.Throw<InstanceSecretContent>(ex));
            }

            // Rights first, as the caller. Presence is decided only once the catalog has READ the
            // store: before its first listing, "no entry" means "not read yet", not "absent".
            return hub.IsGlobalAdmin(user)
                .TakeDecisionOutsideGate()
                .SelectMany(isAdmin => isAdmin
                    ? catalog.WhenLoaded
                    : Observable.Throw<Unit>(InstanceSecretException.Of(
                        "Only a global administrator can change this secret. Nothing was saved.", "secret.error.notAdmin")))
                .SelectMany(_ => mustExist && catalog.Entry(key) is null
                    ? Observable.Throw<InstanceSecretContent>(InstanceSecretException.Of(
                        $"Nothing is set in the portal for '{key}'.", "secret.error.notSet", ("key", key)))
                    : access.RunAsSystem(() => catalog.Entry(key) is null ? Create() : Update()))
                .Select(content => StatusOf(key, InstanceSecretCatalog.CatalogEntry.From(path, content,
                        protector is null ? null : protector.Unprotect, DateTimeOffset.UtcNow),
                    hub.ServiceProvider.GetService<IConfiguration>()?[key]));
        });

    private static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }
}

/// <summary>
/// The live, decrypted set of portal-set secrets on this installation. A mesh-scoped singleton
/// holding ONE query subscription (<see cref="InstanceSecrets.CatalogQuery"/>, as system), in the
/// shape of <see cref="UiContributionCatalog"/>. Values stay in this process's memory and are handed
/// only to signing and verifying code.
/// </summary>
public sealed class InstanceSecretCatalog : IDisposable
{
    /// <summary>One decrypted entry.</summary>
    /// <param name="Path">The node path.</param>
    /// <param name="Content">The stored content (ciphertext and metadata).</param>
    /// <param name="Current">The decrypted current value, or null when absent or undecryptable.</param>
    /// <param name="Previous">The decrypted previous value while its rotation window is open, else null.</param>
    public sealed record CatalogEntry(string Path, InstanceSecretContent Content, string? Current, string? Previous)
    {
        /// <summary>Whether the entry is disabled (a tombstone that also suppresses a configured value).</summary>
        public bool IsDisabled => Content.Disabled;

        /// <summary>Whether the entry is deleted (ignored, so a configured value applies again).</summary>
        public bool IsDeleted => Content.DeletedAt is not null;

        /// <summary>
        /// Builds an entry from stored content. The previous value is dropped once
        /// <see cref="InstanceSecretContent.PreviousUntil"/> has passed. Pure over the delegate.
        /// </summary>
        public static CatalogEntry From(string path, InstanceSecretContent content, Func<string?, string?>? unprotect, DateTimeOffset now)
        {
            // 🚨 Only `enc:`-tagged ciphertext is ever a value. The protector passes an UNTAGGED
            // input through unchanged (legacy plaintext), so a node written around the write path
            // with a plain value would otherwise be used as a key.
            string? Decrypt(string? stored) =>
                string.IsNullOrEmpty(stored) || unprotect is null || !stored.StartsWith("enc:", StringComparison.Ordinal) ? null
                : unprotect(stored) is { Length: > 0 } plain && !plain.StartsWith("enc:", StringComparison.Ordinal) ? plain
                : null;
            var previousOpen = content.PreviousUntil is not { } until || until > now;
            return new CatalogEntry(path, content,
                Decrypt(content.EncryptedValue),
                previousOpen ? Decrypt(content.PreviousEncryptedValue) : null);
        }
    }

    private readonly IMessageHub hub;
    private readonly ILogger<InstanceSecretCatalog>? logger;
    private readonly System.Reactive.Subjects.BehaviorSubject<ImmutableDictionary<string, CatalogEntry>> state =
        new(ImmutableDictionary<string, CatalogEntry>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));
    // Whether the query's first listing has arrived. Kept beside the state, not folded into it, so
    // "nothing is set" and "not read yet" stay two different facts.
    private readonly System.Reactive.Subjects.BehaviorSubject<bool> loaded = new(false);
    private readonly object gate = new();
    private IDisposable? subscription;

    /// <summary>Created by DI, once per mesh.</summary>
    public InstanceSecretCatalog(IMessageHub hub, ILogger<InstanceSecretCatalog>? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
    }

    /// <summary>Emits whenever the set changes (and once on subscribe). Carries no values.</summary>
    public IObservable<Unit> Changes
    {
        get
        {
            EnsureSubscribed();
            return state.Select(_ => Unit.Default);
        }
    }

    /// <summary>
    /// Emits once, when the catalog's first listing has arrived — immediately if it already has.
    /// A verifier waits on this so the first delivery after a start is checked against the
    /// portal-set keys too, not against configuration alone.
    /// </summary>
    public IObservable<Unit> WhenLoaded
    {
        get
        {
            EnsureSubscribed();
            return loaded.Where(l => l).Take(1).Select(_ => Unit.Default);
        }
    }

    /// <summary>Starts the catalog's query now rather than at its first read. Idempotent.</summary>
    public void Start() => EnsureSubscribed();

    /// <summary>The entry for <paramref name="configKey"/>, or null when the portal holds none.</summary>
    public CatalogEntry? Entry(string configKey)
    {
        EnsureSubscribed();
        return state.Value.TryGetValue(configKey.Trim(), out var entry) ? Refresh(entry) : null;
    }

    /// <summary>
    /// True once the catalog's live query has FAULTED. The snapshot can then no longer see a
    /// revocation, so a verifier must fail CLOSED (<see cref="InstanceSecrets.Candidates"/> offers no
    /// per-sender key at all) until the process restarts and the subscription is re-established.
    /// </summary>
    public bool Faulted { get; private set; }

    /// <summary>Every entry whose key is a direct child of <paramref name="section"/>, keyed by the child name.</summary>
    public IEnumerable<(string Child, CatalogEntry Entry)> ChildrenOf(string section)
    {
        EnsureSubscribed();
        var prefix = section.Trim() + ":";
        foreach (var (key, entry) in state.Value)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var child = key[prefix.Length..];
            if (child.Length > 0 && !child.Contains(':'))
                yield return (child, Refresh(entry));
        }
    }

    // A rotation window can close between two catalog emissions; re-derive the previous value at read time.
    private static CatalogEntry Refresh(CatalogEntry entry) =>
        entry.Previous is not null && entry.Content.PreviousUntil is { } until && until <= DateTimeOffset.UtcNow
            ? entry with { Previous = null }
            : entry;

    private void EnsureSubscribed()
    {
        if (subscription is not null)
            return;
        lock (gate)
        {
            if (subscription is not null)
                return;
            var meshService = hub.ServiceProvider.GetService<IMeshService>();
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            if (meshService is null)
            {
                subscription = System.Reactive.Disposables.Disposable.Empty;
                loaded.OnNext(true);
                return;
            }
            // RunAsSystem, not a `using` scope around Subscribe: the subscription outlives this
            // method, and the seal stamps the system identity on the query operation itself.
            subscription = accessService
                .RunAsSystem(() => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(InstanceSecrets.CatalogQuery)))
                .Subscribe(OnChange, ex =>
                {
                    // 🚨 Fail CLOSED: a dead feed can no longer deliver a revocation, so every
                    // per-sender key stops verifying (Candidates) until a restart re-subscribes.
                    Faulted = true;
                    logger?.LogError(ex,
                        "[InstanceSecrets] catalog query FAULTED; per-sender keys are refused until this process restarts");
                    loaded.OnNext(true);
                    state.OnNext(state.Value);
                });
        }
    }

    private void OnChange(QueryResultChange<MeshNode> change)
    {
        var current = state.Value;
        switch (change.ChangeType)
        {
            case QueryChangeType.Initial:
            case QueryChangeType.Reset:
                current = current.Clear();
                foreach (var node in change.Items ?? [])
                    current = Fold(current, node);
                break;
            case QueryChangeType.Added:
            case QueryChangeType.Updated:
                foreach (var node in change.Items ?? [])
                    current = Fold(current, node);
                break;
            case QueryChangeType.Removed:
                foreach (var node in change.Items ?? [])
                    if (node?.Path is { } path)
                        current = current.RemoveRange(current.Where(kv => string.Equals(kv.Value.Path, path, StringComparison.OrdinalIgnoreCase))
                            .Select(kv => kv.Key).ToList());
                break;
        }
        state.OnNext(current);
        if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset && !loaded.Value)
            loaded.OnNext(true);
    }

    private ImmutableDictionary<string, CatalogEntry> Fold(ImmutableDictionary<string, CatalogEntry> current, MeshNode? node)
    {
        if (node?.Path is not { Length: > 0 } path)
            return current;
        var content = node.ContentAs<InstanceSecretContent>(hub.JsonSerializerOptions, logger);
        if (content is null || string.IsNullOrWhiteSpace(content.ConfigKey))
            return current;
        // 🚨 The node must sit where its key says. A node elsewhere under Admin claiming a key it is
        // not filed under would otherwise shadow the real one.
        if (!string.Equals(path, InstanceSecrets.PathOf(content.ConfigKey), StringComparison.OrdinalIgnoreCase))
            return current;
        var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
        var entry = CatalogEntry.From(path, content, protector is null ? null : protector.Unprotect, DateTimeOffset.UtcNow);
        if (content.EncryptedValue is not null && entry.Current is null)
            logger?.LogWarning(
                "[InstanceSecrets] {ConfigKey} is stored but could not be decrypted on this installation (master key missing or changed); it is not used",
                content.ConfigKey);
        return current.SetItem(content.ConfigKey.Trim(), entry);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        subscription?.Dispose();
        state.OnCompleted();
        state.Dispose();
        loaded.OnCompleted();
        loaded.Dispose();
    }
}
