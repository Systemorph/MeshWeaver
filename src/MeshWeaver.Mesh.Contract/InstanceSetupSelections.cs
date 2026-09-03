using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh;

/// <summary>
/// Which logins this instance offers, as the first-run wizard recorded them — the section
/// <c>SignInSetupTab</c> named as the missing half of its own design: <i>"it does not write the
/// answer into the instance's own record … the record that should hold it is the instance manifest,
/// which has no sign-in section yet; adding one is a platform change"</i>. This is that change.
///
/// <para>🚨 <b>Sign-in cannot live in the mesh, and that is not a shortcut.</b> Authentication
/// schemes are registered ONCE while the host is being built — before any storage is open, before
/// any node is read, and before any user is authenticated. A credential the handler needs at
/// <c>AddAuthentication</c> time therefore cannot be a mesh node, however much it would prefer to
/// be one. It lives here, in the pre-storage artifact, for exactly the reason the storage selection
/// does.</para>
///
/// <para><b>Configuration still wins.</b> This section is projected UNDER the host's own
/// configuration sources, so a deployment that states <c>Authentication__Microsoft__ClientId</c> in
/// a ConfigMap or an env var keeps it. The manifest answers what configuration has not already
/// said — the same rule <see cref="InstanceManifest.Storage"/> obeys.</para>
/// </summary>
public sealed record InstanceSignInSelection
{
    /// <summary>
    /// Whether the built-in developer login is on. A switch, not a credential: it has no client id
    /// and no secret, and it self-provisions the signing-in user.
    ///
    /// <para>🚨 Never on an instance the internet can reach. The wizard offers it because a laptop
    /// install with no identity provider is the common case, and refusing it would send someone to
    /// Entra to sign into their own machine.</para>
    /// </summary>
    public bool EnableDevLogin { get; init; }

    /// <summary>
    /// The ids granted platform admin when they sign in through the developer login, comma
    /// separated.
    ///
    /// <para>🚨 A PAIR with <see cref="EnableDevLogin"/>. One without the other is a portal you can
    /// log into and then administer nothing on — the exact state
    /// <c>deploy/homebrew/share/values.local.yaml</c> warns about.</para>
    /// </summary>
    public string? DevAdminUsers { get; init; }

    /// <summary>
    /// The external OAuth/OIDC providers turned on, keyed by the scheme name
    /// <c>/auth/login?provider=</c> takes. Only providers the IMAGE can actually serve appear here;
    /// the wizard offers the <c>SignInProviderCatalog</c> set and nothing else.
    /// </summary>
    public ImmutableList<InstanceSignInProvider> Providers { get; init; } = [];
}

/// <summary>
/// One external sign-in provider as the operator configured it.
///
/// <para>🚨 <see cref="ClientSecret"/> is <c>enc:v1:</c>-protected at rest and is the only field
/// here that is a secret. <see cref="ClientId"/> and <see cref="TenantId"/> are public — a client id
/// travels in every redirect URL — and are stored in the clear deliberately, so that reading the
/// manifest tells an operator what is configured without decrypting anything.</para>
/// </summary>
public sealed record InstanceSignInProvider
{
    /// <summary>The scheme name — <c>Microsoft</c>, <c>Google</c>, <c>LinkedIn</c>, <c>Apple</c>,
    /// <c>GitHub</c>. Exactly the value <c>/auth/login?provider=</c> takes.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The configuration section this provider's keys live under, in colon form
    /// (<c>Authentication:Microsoft</c>, <c>GitHub:OAuth</c>).
    ///
    /// <para>🚨 <b>Written by the wizard from <c>SignInProviderCatalog</c>, not derived.</b> The
    /// shape is NOT uniform — GitHub's hand-rolled OAuth endpoints read <c>GitHub:OAuth</c>, not
    /// <c>Authentication:GitHub</c> — so a rule that composed it from the name would be a second
    /// implementation free to drift from the catalog that registers the handlers. Blank falls back
    /// to <c>Authentication:{Name}</c>, for a manifest written by hand.</para>
    /// </summary>
    public string? Section { get; init; }

    /// <summary>The OAuth client id. Public, and stored in the clear.</summary>
    public string ClientId { get; init; } = "";

    /// <summary>
    /// The tenant, for the one provider that has one.
    ///
    /// <para>🚨 Blank must reach the host as the WORD <c>common</c>, never as <c>""</c>: an empty
    /// <c>Authentication__Microsoft__TenantId</c> composes the authority
    /// <c>login.microsoftonline.com//v2.0</c>, which Entra never serves, and 500-ed every Microsoft
    /// sign-in on 2026-08-28. <c>InstanceSignInProjection</c> substitutes the word.</para>
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The client secret, <c>enc:v1:</c>-encrypted under the install's master key
    /// (<c>Ai:KeyProtection:MasterKey</c>). Never plaintext: <c>ProviderKeyProtector.Protect</c>
    /// throws rather than store an unprotected credential, and the setup surface provisions a
    /// master key before it collects one.
    /// </summary>
    public string? ClientSecret { get; init; }
}

/// <summary>
/// The model providers this instance starts with, and the embeddings endpoint that decides whether
/// vector search is live or dark.
///
/// <para><b>Why the manifest carries LLM keys at all,</b> when the documented home for a provider
/// credential is a <c>Provider/{Name}</c> mesh node: at the moment the wizard runs there is no mesh
/// to put one in. This section is the BOOTSTRAP ENVELOPE, not the vault — the built-in provider is
/// a sync source (<c>BuiltInLanguageModelProvider</c> → <c>ModelStaticRepoSource</c> imports the
/// catalog into the <c>Provider</c> partition on first boot), so a key projected into configuration
/// here lands in its proper node the first time the instance has storage to hold one.</para>
/// </summary>
public sealed record InstanceAiSelection
{
    /// <summary>The providers the operator supplied a key for. Absent means the instance starts
    /// with no model — a valid configuration, and one the portal already warns about at boot.</summary>
    public ImmutableList<InstanceAiProvider> Providers { get; init; } = [];

    /// <summary>
    /// The embeddings endpoint, or null for none.
    ///
    /// <para>🚨 <b>Null is why a fresh SQLite install searches lexically and nobody can tell.</b>
    /// <c>SqliteStorageAdapter</c> writes <c>embedding = NULL</c> with no <c>ITextEmbedder</c>
    /// wired, and <c>SqliteVectorMeshQuery.Matches</c> then answers false, so the vector provider
    /// contributes nothing and search silently degrades — no error, no log line, just worse
    /// results. The wizard asks for this because the default backend needs it.</para>
    /// </summary>
    public InstanceEmbeddingsSelection? Embeddings { get; init; }
}

/// <summary>One model provider and its credential, as the wizard collected it.</summary>
public sealed record InstanceAiProvider
{
    /// <summary>
    /// The provider name, matching the configuration section the provider package binds —
    /// <c>Anthropic</c>, <c>OpenAI</c>, <c>AzureFoundry</c>, <c>OpenAICompatible</c>. The wizard
    /// offers what the image ships and nothing else.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The configuration section this provider binds, from its own
    /// <c>LanguageModelCatalogSource.SectionName</c> — the value the provider package itself
    /// registered, so the wizard cannot name a section the provider does not read. Blank falls back
    /// to <see cref="Name"/>, which is what every first-party provider uses.
    /// </summary>
    public string? Section { get; init; }

    /// <summary>
    /// The API key, <c>enc:v1:</c>-encrypted under the install's master key. Null for the
    /// providers that have none — a local Ollama, the co-hosted Claude Code CLI, Copilot.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>The endpoint, for the providers that take one (Azure Foundry, any
    /// OpenAI-compatible server such as Ollama or vLLM). Not a secret.</summary>
    public string? Endpoint { get; init; }

    /// <summary>The models to offer, when the provider does not discover them itself.</summary>
    public ImmutableList<string> Models { get; init; } = [];
}

/// <summary>
/// Where embeddings come from — an OpenAI-compatible <c>/v1/embeddings</c> endpoint and the model
/// to call. On a laptop this is the Ollama the installer already set up.
/// </summary>
public sealed record InstanceEmbeddingsSelection
{
    /// <summary>The OpenAI-compatible base, e.g. <c>http://localhost:11434/v1</c>.</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>The embedding model, e.g. <c>bge-m3</c> (1024d) or <c>nomic-embed-text</c> (768d).</summary>
    public string Model { get; init; } = DefaultModel;

    /// <summary>The vector dimension, when it cannot be inferred from <see cref="Model"/>.</summary>
    public int? Dimensions { get; init; }

    /// <summary>The API key, <c>enc:v1:</c>-encrypted; null for a local server that wants none.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The default embedding model the wizard pre-fills — the one
    /// <c>AddSqliteOllamaEmbeddings</c> itself defaults to, so the wizard and the code it configures
    /// cannot disagree.</summary>
    public const string DefaultModel = "bge-m3";

    /// <summary>Whether this selection names an endpoint at all. An endpoint-less selection is the
    /// same as none, and must never be projected as a configured embedder.</summary>
    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}
