using MeshWeaver.AI;

namespace MeshWeaver.Mesh;

/// <summary>
/// The instance manifest read as CONFIGURATION — the one place that knows which key each of the
/// wizard's answers lands on.
///
/// <para>🚨 <b>Pure, and it decrypts.</b> Nothing here reads a file, a hub or an
/// <c>IConfiguration</c>: it takes a manifest and a protector and returns the entries. That is what
/// lets every rule below be asserted without booting a host — and the rules are the whole value,
/// because each one fails SILENTLY when it is wrong: a blank tenant that composes an authority
/// Entra never serves, a client id absent rather than empty (which a fleet record reads as "not
/// stated" and inherits back ON), an embeddings endpoint missing so vector search goes dark with no
/// error anywhere.</para>
///
/// <para><b>Lowest priority, always.</b> These entries are inserted BELOW the host's own
/// configuration sources, so an appsettings value, a ConfigMap key or an environment variable wins
/// over the manifest every time. The manifest answers what configuration has not already said; a
/// mechanism that could overwrite a deployment's stated storage or credential would be a data-loss
/// bug rather than a feature, which is the rule <see cref="InstanceManifest"/> already states for
/// storage and this extends to every section.</para>
///
/// <para><b>Only a COMPLETE manifest is projected.</b> A half-answered wizard
/// (<c>AwaitingStorage</c>, <c>AwaitingModules</c>) and an <c>Unreadable</c> one answer nothing:
/// projecting their pre-filled defaults would boot the instance past its own setup surface on
/// answers nobody gave.</para>
/// </summary>
public static class InstanceManifestProjection
{
    /// <summary>The configuration section the storage selection lands on.</summary>
    public const string StorageSection = "Graph:Storage";

    /// <summary>The section the developer-login switch and the external providers land under.</summary>
    public const string AuthenticationSection = "Authentication";

    /// <summary>The section the embeddings selection lands on — singular, as
    /// <c>EmbeddingOptions</c> binds it.</summary>
    public const string EmbeddingSection = "Embedding";

    /// <summary>The section the registered identity lands on — what the plugin catalog reads to
    /// know who this instance is and which key it fetches with.</summary>
    public const string PluginCatalogSection = "PluginCatalog";

    /// <summary>The section carrying the container-registry credential derived from the instance
    /// key.</summary>
    public const string ContainerRegistrySection = "ContainerRegistry";

    /// <summary>
    /// The fleet's OCI registry, which authenticates an instance as Basic <c>instance:&lt;key&gt;</c>.
    /// </summary>
    public const string ContainerRegistryHost = "cr.meshweaver.cloud";

    /// <summary>
    /// The tenant value a blank Microsoft tenant must become.
    ///
    /// <para>🚨 The WORD, never <c>""</c>. An empty <c>Authentication__Microsoft__TenantId</c>
    /// composes <c>login.microsoftonline.com//v2.0</c> — a URL Entra never serves — and 500-ed every
    /// Microsoft sign-in on 2026-08-28.</para>
    /// </summary>
    public const string MultiTenant = "common";

    /// <summary>
    /// Every configuration entry a completed manifest supplies. An incomplete, unreadable or null
    /// manifest yields none.
    /// </summary>
    /// <param name="manifest">The manifest to project. Null yields an empty result.</param>
    /// <param name="protector">Decrypts the <c>enc:v1:</c> secrets. When null, encrypted values are
    /// OMITTED rather than emitted encrypted — a handler handed ciphertext as its client secret
    /// fails at the token exchange, four steps from the missing master key that caused it.</param>
    /// <returns>The entries, keyed in colon form.</returns>
    public static IReadOnlyDictionary<string, string?> ToConfiguration(
        InstanceManifest? manifest, IProviderKeyProtector? protector)
    {
        var entries = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (manifest is not { State: InstanceSetupState.Complete })
            return entries;

        ProjectIdentity(manifest.Identity, protector, entries);
        ProjectStorage(manifest.Storage, protector, entries);
        ProjectSignIn(manifest.SignIn, protector, entries);
        ProjectAi(manifest.Ai, protector, entries);
        return entries;
    }

    /// <summary>
    /// The registered identity, as the plugin catalog reads it.
    ///
    /// <para>🚨 <b><c>RegistryToken</c> is the load-bearing one, and without it the wizard's whole
    /// registration is ORPHANED.</b> <c>InstanceAutoRegistrationService</c> runs on the configured
    /// boot and registers whenever it sees an instance id and no token — so an instance that had
    /// just registered through the wizard would register a SECOND time, under whatever id the
    /// deployment happens to carry, and claim another id permanently (ids are global and never
    /// re-issued). Projecting the token instead takes that service's own
    /// <c>RegistrationPreflight.Skip</c> branch — <i>"a registry token is already configured — the
    /// explicit token wins"</i> — so the boot uses the registration the operator already made.</para>
    ///
    /// <para>Nothing is projected until the key can be DECRYPTED. A registry token that is still
    /// ciphertext authenticates nothing: every fetch would 401, the catalog would look empty, and
    /// the instance would appear to have been granted nothing — while the id sits claimed. Dropping
    /// the whole identity instead leaves auto-registration to report the real problem.</para>
    /// </summary>
    private static void ProjectIdentity(
        InstanceIdentitySelection? identity, IProviderKeyProtector? protector,
        IDictionary<string, string?> entries)
    {
        if (identity is not { IsRegistered: true })
            return;
        if (Reveal(identity.InstanceKey, protector) is not { } key)
            return;

        entries[$"{PluginCatalogSection}:RegistryUrl"] = identity.RegistryUrl;
        entries[$"{PluginCatalogSection}:InstanceId"] = identity.Id;
        entries[$"{PluginCatalogSection}:RegistryToken"] = key;

        // 🚨 The SAME key, in the shape a container runtime reads. The fleet's OCI registry
        // (cr.meshweaver.cloud) authenticates an instance as Basic `instance:<key>` — so an install
        // that must pull images and, in time, plugin bundles from it needs a docker config, and the
        // only credential it has is this one. Deriving it HERE rather than in the CLI is what keeps
        // it one credential written once: memex-local, a self-hosted install and the portal itself
        // all read the same manifest value, and nothing has to be kept in step by hand.
        entries[$"{ContainerRegistrySection}:DockerConfigJson"] = DockerConfigJson(key);
    }

    private static void ProjectStorage(
        InstanceStorageSelection? storage, IProviderKeyProtector? protector,
        IDictionary<string, string?> entries)
    {
        if (storage is null || string.IsNullOrWhiteSpace(storage.Type))
            return;

        entries[$"{StorageSection}:Type"] = storage.Type;
        if (!string.IsNullOrWhiteSpace(storage.BasePath))
            entries[$"{StorageSection}:BasePath"] = storage.BasePath;
        // 🚨 The connection string CARRIES A PASSWORD, so it is revealed exactly like every other
        // secret in this file. An earlier comment here claimed it could not be encrypted because
        // "the host needs it before the master key can be of any use" — that was simply wrong: the
        // master key is resolved from the deployment or from instance.key BEFORE this projection
        // runs, which is how the sign-in secrets and provider keys in the SAME manifest are already
        // encrypted. Leaving this one in the clear meant a manifest that is copied onto a new
        // volume, backed up, or pasted into an issue carried a live database password in plaintext
        // beside ciphertext that was carefully protected.
        //
        // Reveal() passes an UNENCRYPTED value through unchanged, so a manifest written before this
        // (or hand-authored) keeps working.
        if (Reveal(storage.ConnectionString, protector) is { } connectionString)
            entries[$"{StorageSection}:ConnectionString"] = connectionString;
    }

    private static void ProjectSignIn(
        InstanceSignInSelection? signIn, IProviderKeyProtector? protector,
        IDictionary<string, string?> entries)
    {
        if (signIn is null)
            return;

        entries[$"{AuthenticationSection}:EnableDevLogin"] = signIn.EnableDevLogin ? "true" : "false";
        if (!string.IsNullOrWhiteSpace(signIn.DevAdminUsers))
            entries[$"{AuthenticationSection}:DevAdminUsers"] = signIn.DevAdminUsers;

        foreach (var provider in signIn.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
                continue;
            var section = SectionOf(provider);

            // 🚨 An EMPTY client id, never an absent key, is how a provider is turned OFF. An
            // absent key means "not stated", which on a fleet record inherits the template's value
            // and quietly turns the provider back on; "" is the deployed shape of off, and every
            // handler's own IsNullOrEmpty gate reads it that way.
            entries[$"{section}:ClientId"] = provider.ClientId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(provider.ClientId))
                continue;

            if (provider.TenantId is not null || RequiresTenant(provider.Name))
                entries[$"{section}:TenantId"] =
                    string.IsNullOrWhiteSpace(provider.TenantId) ? MultiTenant : provider.TenantId.Trim();

            if (Reveal(provider.ClientSecret, protector) is { } secret)
                entries[$"{section}:ClientSecret"] = secret;
        }
    }

    private static void ProjectAi(
        InstanceAiSelection? ai, IProviderKeyProtector? protector,
        IDictionary<string, string?> entries)
    {
        if (ai is null)
            return;

        foreach (var provider in ai.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
                continue;
            var section = string.IsNullOrWhiteSpace(provider.Section)
                ? provider.Name.Trim()
                : provider.Section.Trim();

            if (Reveal(provider.ApiKey, protector) is { } key)
                entries[$"{section}:ApiKey"] = key;
            if (!string.IsNullOrWhiteSpace(provider.Endpoint))
                entries[$"{section}:Endpoint"] = provider.Endpoint.Trim();
            // Models bind as an array; the indexed colon form is what an IConfiguration array is.
            for (var i = 0; i < provider.Models.Count; i++)
                entries[$"{section}:Models:{i}"] = provider.Models[i];
        }

        if (ai.Embeddings is not { IsConfigured: true } embeddings)
            return;

        // "OpenAICompatible" is the EmbeddingOptions.Provider value that selects the
        // OllamaEmbeddingProvider against an OpenAI-compatible /v1/embeddings base — the only
        // backend the wizard can configure without an Azure subscription, and the one a laptop has.
        entries[$"{EmbeddingSection}:Provider"] = "OpenAICompatible";
        entries[$"{EmbeddingSection}:Endpoint"] = embeddings.Endpoint.Trim();
        entries[$"{EmbeddingSection}:Model"] = embeddings.Model;
        if (embeddings.Dimensions is { } dimensions)
            entries[$"{EmbeddingSection}:Dimensions"] = dimensions.ToString();
        if (Reveal(embeddings.ApiKey, protector) is { } embedKey)
            entries[$"{EmbeddingSection}:ApiKey"] = embedKey;
    }

    /// <summary>
    /// A docker <c>config.json</c> carrying this instance's key for the fleet registry.
    ///
    /// <para>The username is the literal <c>instance</c> and the password is the instance key — the
    /// registry's docker_auth resolves the key to the instance's grants, so the id does not need to
    /// be in the credential. Base64 of <c>user:password</c> is the auth field's defined shape; it is
    /// an ENCODING, not encryption, which is exactly why this value only ever appears in
    /// configuration derived from an already-decrypted key and never in the manifest on disk.</para>
    /// </summary>
    /// <param name="instanceKey">The decrypted instance key.</param>
    private static string DockerConfigJson(string instanceKey)
    {
        var auth = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"instance:{instanceKey}"));
        // Hand-built rather than serialized: the shape is two nested objects and a string, and a
        // serializer here would pull a dependency into a contract assembly for no benefit. The only
        // interpolated value is base64, which cannot contain a quote or a backslash.
        return $"{{\"auths\":{{\"{ContainerRegistryHost}\":{{\"auth\":\"{auth}\"}}}}}}";
    }

    /// <summary>
    /// The configuration section a sign-in provider's keys live under. The wizard writes the
    /// section it read from <c>SignInProviderCatalog</c>, because the shape is NOT uniform —
    /// GitHub's is <c>GitHub:OAuth</c>, not <c>Authentication:GitHub</c>. Deriving it here instead
    /// would be a second implementation of one rule, free to drift from the catalog that registers
    /// the handlers; the fallback exists only for a hand-written manifest.
    /// </summary>
    private static string SectionOf(InstanceSignInProvider provider) =>
        string.IsNullOrWhiteSpace(provider.Section)
            ? $"{AuthenticationSection}:{provider.Name.Trim()}"
            : provider.Section.Trim();

    /// <summary>
    /// Whether a provider must always carry a tenant entry. Only Microsoft has one, and it must be
    /// stated even when the operator left it blank — see <see cref="MultiTenant"/>.
    /// </summary>
    private static bool RequiresTenant(string name) =>
        string.Equals(name.Trim(), "Microsoft", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The usable value of a stored secret, or null when there is nothing to project.
    ///
    /// <para>🚨 An <c>enc:</c> value with NO protector is dropped, never passed through. Emitting
    /// ciphertext as a client secret or an API key produces a provider that registers, renders its
    /// button, and fails at the token exchange with an error naming the endpoint rather than the
    /// missing master key — the failure landing furthest from its cause.</para>
    /// </summary>
    private static string? Reveal(string? stored, IProviderKeyProtector? protector)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        if (!stored.StartsWith("enc:", StringComparison.Ordinal))
            return stored;
        if (protector is null)
            return null;
        var revealed = protector.Unprotect(stored);
        // Unprotect answers the input unchanged when it cannot decrypt (a rotated or truncated
        // master key). Still ciphertext ⇒ still unusable ⇒ still dropped.
        return string.IsNullOrEmpty(revealed) || revealed.StartsWith("enc:", StringComparison.Ordinal)
            ? null
            : revealed;
    }
}
