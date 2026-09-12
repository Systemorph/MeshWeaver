using System.Reactive.Linq;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// Lists a repository's tags on an <b>OCI Distribution</b> registry — the fleet's own registry, or
/// the read-through mirror another installation serves at <c>{portal}/v2</c> (#3353,
/// <c>Doc/Architecture/ContainerRegistryInMemex</c>) — for a self-updater whose
/// <see cref="SelfUpdateOptions.Registry"/> is NOT an Azure Container Registry.
///
/// <para><b>The credential is the one this installation already holds.</b> The registry authenticates
/// a caller with its plugin-registry instance key, and a consuming installation already carries
/// exactly that key for the registry it installs modules from
/// (<c>PluginCatalog:Registries:N:Token</c> / legacy <c>PluginCatalog:RegistryToken</c>, or the
/// stored auto-registration credential). So the credential is RESOLVED, never configured a second
/// time, through the same <see cref="RegistryTokenResolver"/> the Store uses. No new secret,
/// nothing to rotate twice.</para>
///
/// <para>🚨 <b>WHICH plugin registry's key is a disclosure decision</b>, and <see cref="ResolveCredential"/>
/// is the control: the container registry's OWN host when a plugin registry is configured there,
/// or the host this installation DECLARES as that registry's validator
/// (<see cref="SelfUpdateOptions.RegistryValidationUrl"/>) when one is configured there — and
/// nothing else. No declaration ⇒ refuse. The fleet needs the second case because
/// <c>cr.meshweaver.cloud</c> decides a pull by forwarding the key to
/// <c>memex.meshweaver.cloud</c>, so the two hosts differ BY DESIGN (#4093,
/// <c>Doc/Architecture/SelfUpdateRegistryCredential</c>).</para>
///
/// <para><b>The wire conversation</b> — the bearer handshake, the paginated listing, every page
/// followed — is <see cref="OciRegistryClient.ListTags"/>, the same client
/// <see cref="PluginBundleClient"/> pulls bundles with; this class contributes the credential
/// resolution and the self-update-specific configuration errors.</para>
///
/// <para><b>Failure is an error, never an empty list.</b> A refused credential, an unreachable
/// host or a malformed answer FAULT the observable and the poller records the fault — an empty
/// listing would be read as "nothing to roll to", which is the one silent state #2553 exists to
/// forbid. Compare <c>UnavailableUpdateMechanics.NoRegistry</c>, which answers empty by design
/// because it stands for "no registry configured at all".</para>
///
/// <para>Selected by the poller from <see cref="SelfUpdateOptions.RegistryIsAzureContainerRegistry"/>;
/// the ACR path is untouched.</para>
/// </summary>
public sealed class OciTagLister(
    IMessageHub hub,
    SelfUpdateOptions options,
    ILogger<OciTagLister>? logger = null)
{
    /// <summary>The page size asked for. The registry may answer fewer; every page is followed.</summary>
    public const int PageSize = OciRegistryClient.TagPageSize;

    /// <summary>The named HttpClient a host may configure; the shared fallback is used otherwise.</summary>
    public const string HttpClientName = "MeshWeaver.SelfUpdate.Oci";

    /// <summary>
    /// Every tag on <paramref name="repository"/> at <see cref="SelfUpdateOptions.Registry"/>.
    /// Cold; emits once; FAULTS on any answer that is not a complete listing.
    /// </summary>
    public IObservable<IReadOnlyList<string>> ListTags(string repository)
    {
        var configured = (options.Registry ?? string.Empty).Trim().TrimEnd('/');
        if (configured.Length == 0)
            return Observable.Throw<IReadOnlyList<string>>(new InvalidOperationException(
                "SelfUpdate:Registry is empty — there is no registry to list tags on."));

        // 🚨 THE TARGET IS NORMALIZED AND VALIDATED BEFORE ANY CREDENTIAL IS RESOLVED, and the
        // check is that the configured value ALREADY IS a bare `host[:port]`.
        //
        // Two defects live behind a raw value here, and the second is a key disclosure:
        //  * ASYMMETRY — `ResolveCredential` compares this against plugin-registry hosts parsed by
        //    `HostOf`, so a `https://host` form would miss a registry that is in fact the same host.
        //  * DISCLOSURE — `OciRegistryClient` builds `https://{registry}/` for a value with no
        //    scheme, so `instance:mwi_…@evil.example` is a VALID URI whose host is `evil.example`;
        //    it would receive the Basic credential, and the raw value is interpolated into that
        //    client's error messages and into this class's audit line. Host equality alone could
        //    never match such a value, so the declared-validator branch below is what would make it
        //    reachable — this refusal is the reason that branch cannot open a disclosure path.
        //
        // Requiring a bare host (not merely "parses to a host") also keeps this in step with
        // SelfUpdateOptions.PortalImage/MigrationImage, which interpolate the SAME value into an
        // image reference and need a bare host for it to be well-formed.
        var registry = SelfUpdateOptions.HostOf(configured);
        if (registry is null)
            // 🚨 The value is NOT echoed: this is exactly the branch a userinfo-bearing value lands
            // in, and the userinfo is where a key would be.
            return Observable.Throw<IReadOnlyList<string>>(new InvalidOperationException(
                "SelfUpdate:Registry does not name an http(s) registry host. It must be a bare "
                + "'host' or 'host:port' (for example cr.meshweaver.cloud) — never a value carrying "
                + "credentials, and never one this platform cannot also use as the registry half of "
                + "an image reference. The configured value is not repeated here because a value of "
                + "this shape can carry a credential."));
        // 🚨 "Bare" is a TEXTUAL test on the configured value, NOT equality with the host HostOf
        // read from it: HostOf drops a scheme-default port, so `cr.example.test:443` — a legal
        // `host:port` the documentation promises — would fail an equality test and be refused with
        // a message claiming it carries a scheme or a path when it carries neither (#4094 review).
        // A value that parsed (so: no userinfo) and carries no scheme and no path IS a bare host,
        // whatever port it names; the client and the messages then use the normalized host.
        if (!IsBareHost(configured))
            // Safe to name: HostOf refuses userinfo, so a value that parsed cannot have carried one.
            return Observable.Throw<IReadOnlyList<string>>(new InvalidOperationException(
                $"SelfUpdate:Registry must be a bare registry host, but it carries a scheme or a path; "
                + $"it names the host '{registry}'. Set SelfUpdate:Registry to '{registry}' — the same "
                + "value is interpolated into the portal and migration image references, which are "
                + "malformed with anything else."));

        return new OciRegistryClient(
                hub, registry, ResolveCredential(registry), options.RegistryUsername, HttpClientName, logger)
            .ListTags(repository);
    }

    /// <summary>
    /// Whether a value that <see cref="SelfUpdateOptions.HostOf"/> could read is ALSO nothing but
    /// <c>host[:port]</c>: no scheme, no path, no query, no fragment. Userinfo is not tested here
    /// because HostOf already refused it. Pure.
    /// </summary>
    private static bool IsBareHost(string configured) =>
        !configured.Contains("://", StringComparison.Ordinal)
        && configured.IndexOfAny(['/', '\\', '?', '#']) < 0;

    /// <summary>
    /// 🚨 The plugin-registry credential this installation may present to the container registry —
    /// and the DISCLOSURE CONTROL that decides which one, because the answer is an instance key and
    /// the wrong answer hands it to whatever host <c>SelfUpdate:Registry</c> happens to name.
    ///
    /// <para>Exactly two hosts qualify, both by an explicit statement in this installation's own
    /// configuration, tried in this order:</para>
    /// <list type="number">
    /// <item>the container registry's OWN host, when a plugin registry is configured there — a
    /// portal serving its images from the <c>/v2</c> mirror it also serves its catalog from;</item>
    /// <item>the host this installation DECLARES as that registry's validator
    /// (<see cref="SelfUpdateOptions.RegistryValidationUrl"/>), when a plugin registry is
    /// configured THERE — the fleet's shape, where <c>cr.meshweaver.cloud</c> forwards the key to
    /// <c>memex.meshweaver.cloud</c> to decide the pull (#4093,
    /// <c>Doc/Architecture/SelfUpdateRegistryCredential</c>).</item>
    /// </list>
    ///
    /// <para>Nothing else, ever. The two hosts are compared WHOLE and case-insensitively — never by
    /// suffix, registrable domain or any other resemblance, which is a coincidence of naming rather
    /// than a grant — and an ABSENT declaration is refused, never read as permission. Both steps
    /// need the operator to have configured a plugin registry on the host in question, so the key
    /// only ever goes where this installation was already given one for.</para>
    ///
    /// <para>The SHAPE of the credential follows the host: the registry's own host gets what the
    /// Store presents (<see cref="RegistryTokenResolver.ResolveToken"/> — a short-lived token when
    /// the stored key was exchanged for one); a declared validator gets the DURABLE key
    /// (<see cref="RegistryTokenResolver.ResolveDurableKey"/>), because on the fleet's shape the
    /// validator IS the key-to-token exchange and refuses a token by design.</para>
    ///
    /// <para>Faults, naming the hosts (never the key), when the declaration is set but unreadable,
    /// when no host qualifies, when the only registry on a qualifying host carries its credential
    /// inside its URL, or when the resolved credential is empty: a listing without a credential
    /// could only ever be a 401, so the misconfiguration is said once here instead of on every
    /// check as a refused handshake. Cold: the fault is raised at subscription, before the client
    /// sends a byte.</para>
    /// </summary>
    private IObservable<string> ResolveCredential(string registry) =>
        Observable.Defer(() =>
        {
            // 🚨 A DECLARED-BUT-UNREADABLE validator is refused as MALFORMED, first and on its own —
            // never folded into "nothing declared". `RegistryValidatorHost` is null for both, and
            // treating the two alike diagnosed a typo'd scheme or a userinfo-bearing declaration
            // as an ABSENT one: the boot line said "NO validator declared" and the refusal told the
            // operator to set the key they had already set (#4094 review). The value is not echoed —
            // a userinfo-bearing value lands exactly here, and the userinfo is where a key would be.
            if (options.RegistryValidatorDeclared && options.RegistryValidatorHost is null)
                return Observable.Throw<string>(new InvalidOperationException(
                    "SelfUpdate:RegistryValidationUrl is set but does not name an http(s) host, so it "
                    + "declares no validator and the listing refuses. Set it to the registry record's "
                    + "validationUrl verbatim (for example https://memex.meshweaver.cloud/api/instances/token) "
                    + "or to a bare host — never a value carrying credentials. The configured value is "
                    + "not repeated here because a value of this shape can carry a credential."));

            var catalog = hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions();
            var registries = RegistryTokenResolver.WithLegacyTokens(catalog, catalog.EffectiveRegistries);
            var declaredValidator = options.RegistryValidatorHost;

            var match = On(registries, registry);
            // The HOST the match was made on — the parsed host, never `match.Url`, which may carry
            // userinfo; every message and log line below names this and nothing else.
            var matchedHost = registry;
            var paired = false;
            if (match is null && declaredValidator is not null && On(registries, declaredValidator) is { } viaValidator)
            {
                // 🚨 The new, security-relevant decision, said out loud once per listing — and this
                // is an Information line, so it LEAVES THE POD for Loki. It therefore carries TWO
                // HOSTS and the NAME of a configuration key, and NOTHING derived from the secret:
                // not the key, not a prefix or suffix of it, not its length, not a hash of it.
                // Do not add one "for diagnostics" — a length is a fact about a credential.
                //
                // 🚨 And the hosts are the PARSED hosts, never `viaValidator.Url`. A configured URL
                // may carry userinfo (`https://instance:mwi_…@memex.meshweaver.cloud`), which would
                // put the key in Loki in a field nobody thinks of as a credential — the #3201
                // shape, where the registry key sat in a pod spec `kubectl describe` printed and is
                // still owed a rotation. `declaredValidator` IS this registry's host by
                // construction: it is what `On(...)` just matched it by.
                logger?.LogInformation(
                    "[SelfUpdate] presenting the instance key configured under PluginCatalog:Registries "
                    + "for {PluginRegistryHost} to the container registry {Registry}: "
                    + "SelfUpdate:RegistryValidationUrl declares that host as the portal which validates "
                    + "this installation's key there. The durable key is presented, unexchanged — the "
                    + "validator IS the key-to-token exchange and refuses a token.",
                    declaredValidator, registry);
                match = viaValidator;
                matchedHost = declaredValidator;
                paired = true;
            }

            if (match is null)
            {
                // 🚨 A plugin registry that EXISTS on the host but whose URL carries credentials is
                // named as such, never diagnosed as "no plugin registry is configured on that host".
                // HostOf refuses userinfo (so `https://memex.meshweaver.cloud@evil.example` can never
                // be read as memex), which also means a registry configured as
                // `https://instance:mwi_…@host` stops matching on BOTH branches — the pre-#4094
                // reader tolerated it. The registry is real and the catalog is already talking to
                // it; what is wrong is WHERE the credential sits, and the message says so. The URL
                // is not echoed: the credential is in it.
                var credentialInUrl = registries.FirstOrDefault(r =>
                    CarriesCredentialsFor(r.Url, registry)
                    || (declaredValidator is not null && CarriesCredentialsFor(r.Url, declaredValidator)));
                if (credentialInUrl is not null)
                    return Observable.Throw<string>(new InvalidOperationException(
                        $"SelfUpdate:Registry is '{registry}', an OCI registry the self-updater authenticates "
                        + "with this installation's plugin-registry instance key. A plugin registry IS configured "
                        + "for that host or for its declared validator, but its PluginCatalog:Registries URL "
                        + "carries credentials (user:secret@host), and a URL of that shape is never read as "
                        + "naming a host — the credential is where a human misreads the host. Move the key "
                        + "to PluginCatalog:Registries:N:Token and set the URL to the bare "
                        + "https://host; the registry then qualifies. The URL is not repeated here because "
                        + "the credential is in it."));

                return Observable.Throw<string>(new InvalidOperationException(
                    $"SelfUpdate:Registry is '{registry}', an OCI registry the self-updater authenticates "
                    + "with this installation's plugin-registry instance key — but no plugin registry "
                    + "(PluginCatalog:Registries / PluginCatalog:RegistryUrl) is configured on that host, "
                    + (declaredValidator is null
                        ? "and no validator is declared for it, so there is no key to present. Either "
                          + "configure the plugin registry on the same host, or — when the registry "
                          + "validates this installation's key at ANOTHER portal, as cr.meshweaver.cloud "
                          + "does at memex.meshweaver.cloud — declare that portal in "
                          + "SelfUpdate:RegistryValidationUrl (the registry record's own validationUrl) "
                          + "and configure it as a plugin registry. "
                        : $"and the validator it declares, '{declaredValidator}' "
                          + "(SelfUpdate:RegistryValidationUrl), is not configured as one either, so there "
                          + "is no key to present. Add that host to PluginCatalog:Registries with the "
                          + "instance key issued for it. ")
                    + "Or set SelfUpdate:Registry back to the upstream Azure Container Registry."));
            }

            // 🚨 WHICH SHAPE of the credential depends on WHO validates it, and the two branches
            // differ here on purpose:
            //  * the registry's OWN host is a portal serving its /v2 mirror, whose token endpoint
            //    runs the same authenticator the plugin registry does and accepts BOTH the durable
            //    key and the short-lived token — so it is handed what the Store presents
            //    (`ResolveToken`: the configured token, or the stored key EXCHANGED for a JWT);
            //  * a DECLARED validator is, on the fleet's shape, the key-to-token exchange itself
            //    (`/api/instances/token`), and that endpoint refuses a token by design — a token may
            //    never mint its successor. An auto-registered installation (PluginCatalog:BootstrapKey,
            //    no configured Token) resolving through `ResolveToken` would therefore present an
            //    `mwa_` JWT and be refused on EVERY check, while only an installation with a raw
            //    Token configured would ever work (#4094 review). So this branch presents the
            //    DURABLE key with no exchange — the one shape every validator that accepts the
            //    instance key accepts.
            var resolver = hub.ServiceProvider.GetService<RegistryTokenResolver>();
            var credential = resolver is null
                ? Observable.Return(match.Token?.Trim() ?? string.Empty)
                : paired
                    ? resolver.ResolveDurableKey(match)
                    : resolver.ResolveToken(match);

            // 🚨 `matchedHost`, never `match.Url` — same reason as the log line above: a configured
            // URL may carry userinfo, and this message is logged by the poller on every failed check.
            return credential.Select(token => token.Length > 0
                ? token
                : throw new InvalidOperationException(
                    $"No instance key could be resolved for the plugin registry at {matchedHost}, so the "
                    + $"container registry {registry} cannot be listed. The key arrives as "
                    + "PluginCatalog:Registries:N:Token (or the legacy PluginCatalog:RegistryToken), or "
                    + "through auto-registration with PluginCatalog:BootstrapKey."));
        });

    /// <summary>
    /// Whether a configured plugin-registry URL names <paramref name="host"/> BUT carries
    /// credentials (userinfo) — the one shape <see cref="SelfUpdateOptions.HostOf"/> refuses for a
    /// registry that nevertheless exists and is in use. Serves the DIAGNOSIS only: nothing is ever
    /// matched, selected or logged through this reader. Pure.
    /// </summary>
    private static bool CarriesCredentialsFor(string? url, string host) =>
        Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
        && uri.UserInfo.Length > 0
        && uri.Scheme is "http" or "https"
        && string.Equals(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}", host,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The configured plugin registry living on <paramref name="host"/> — whole-host, case
    /// insensitive, through the SAME host reader the declaration is parsed with, so the two sides
    /// of the comparison can never drift. Null when none does. Pure.
    /// </summary>
    private static PluginRegistryReference? On(IEnumerable<PluginRegistryReference> registries, string host) =>
        registries.FirstOrDefault(r => SelfUpdateOptions.HostOf(r.Url) is { } configured
            && string.Equals(configured, host, StringComparison.OrdinalIgnoreCase));
}
