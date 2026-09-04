using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// Every string the first-run wizard shows, resolved for one viewer's locale.
///
/// <para>🚨 <b>The wizard runs before there is a mesh, so it cannot ask
/// <c>AccessContext.Locale</c> — but it still must not hard-code English.</b>
/// <see cref="LocalizationCatalog"/> is a static, immutable table loaded from embedded resources
/// with no hub, no storage and no DI behind it, so it answers perfectly well pre-storage. The
/// locale comes from the request's <c>Accept-Language</c> header, read EXPLICITLY — which is the
/// opposite of the banned <c>CultureInfo.CurrentUICulture</c> resolution, not an exception to
/// it: the value is passed as an argument and never picked up ambiently from the thread.</para>
///
/// <para>An instance rather than statics because the locale is per REQUEST. Two operators can be
/// setting up two instances from one host binary; a static "current language" is exactly the shape
/// of bug the ambient-culture ban exists to prevent.</para>
/// </summary>
/// <param name="locale">The viewer's locale, from <c>Accept-Language</c>. Unknown values resolve to
/// the default through <c>Locales.Resolve</c>.</param>
public sealed class SetupStrings(string? locale)
{
    /// <summary>The locale these strings were resolved for.</summary>
    public string? Locale { get; } = locale;

    /// <summary>Looks a key up in this instance's locale. Missing keys surface as the key itself —
    /// visible in review, harmless in production.</summary>
    /// <param name="key">The catalog key.</param>
    /// <param name="args">Positional arguments, applied invariantly.</param>
    public string Get(string key, params object?[] args) =>
        LocalizationCatalog.Get(key, Locale, args);

    // ── Page chrome ────────────────────────────────────────────────────────────────────────────
    /// <summary>The page title and heading.</summary>
    public string Title => Get("setup.title");
    /// <summary>The one-line explanation under the heading.</summary>
    public string Intro => Get("setup.intro");
    /// <summary>The submit button.</summary>
    public string Submit => Get("setup.submit");
    /// <summary>The heading over the problems list.</summary>
    public string ProblemsHeading => Get("setup.problems");
    /// <summary>The heading over the warnings list.</summary>
    public string WarningsHeading => Get("setup.warnings");
    /// <summary>Shown once the manifest is written and the instance is restarting.</summary>
    public string Done => Get("setup.done");
    /// <summary>Explains that the instance restarts into its configured self.</summary>
    public string DoneDetail => Get("setup.done.detail");

    // ── Step: setup token ──────────────────────────────────────────────────────────────────────
    /// <summary>The access-token field label.</summary>
    public string TokenLabel => Get("setup.token.label");
    /// <summary>Where to find the token.</summary>
    public string TokenHelp => Get("setup.token.help");
    /// <summary>Refusal when the token is absent or wrong.</summary>
    public string TokenInvalid => Get("setup.token.invalid");

    // ── Phase one: identity ────────────────────────────────────────────────────────────────────
    /// <summary>The hello page's title.</summary>
    public string HelloTitle => Get("setup.hello.title");
    /// <summary>Why the name is asked first.</summary>
    public string HelloIntro => Get("setup.hello.intro");
    /// <summary>The identity section heading.</summary>
    public string IdentityHeading => Get("setup.identity.heading");
    /// <summary>What the name and id are for.</summary>
    public string IdentityHelp => Get("setup.identity.help");
    /// <summary>The instance-name field label.</summary>
    public string InstanceNameLabel => Get("setup.identity.name");
    /// <summary>The instance-name placeholder.</summary>
    public string InstanceNamePlaceholder => Get("setup.identity.name.placeholder");
    /// <summary>The generated-id field label.</summary>
    public string InstanceIdLabel => Get("setup.identity.id");
    /// <summary>Why the id cannot be edited.</summary>
    public string InstanceIdHelp => Get("setup.identity.id.help");
    /// <summary>The registry field label.</summary>
    public string RegistryLabel => Get("setup.identity.registry");
    /// <summary>What the registry is for.</summary>
    public string RegistryHelp => Get("setup.identity.registry.help");
    /// <summary>The optional registration-key field label.</summary>
    public string BootstrapKeyLabel => Get("setup.identity.key");
    /// <summary>When a registration key is needed.</summary>
    public string BootstrapKeyHelp => Get("setup.identity.key.help");
    /// <summary>The phase-one submit button.</summary>
    public string RegisterAndContinue => Get("setup.identity.submit");
    /// <summary>The registered identity, shown in phase two.</summary>
    /// <param name="id">The claimed instance id.</param>
    /// <param name="registry">The registry it registered with.</param>
    public string RegisteredAs(string id, string registry) => Get("setup.registeredAs", id, registry);
    /// <summary>The plan the registry enrolled this instance on.</summary>
    /// <param name="plan">The plan name.</param>
    public string PlanIs(string plan) => Get("setup.planIs", plan);

    // ── Step: plugins ──────────────────────────────────────────────────────────────────────────
    /// <summary>The plugins section heading.</summary>
    public string PackagesHeading => Get("setup.packages.heading");
    /// <summary>What the plugin list is.</summary>
    public string PackagesHelp => Get("setup.packages.help");
    /// <summary>Shown when the plan lists nothing — a legitimate answer, said out loud.</summary>
    public string NoPackages => Get("setup.packages.none");
    /// <summary>Marks a package that provides a database backend.</summary>
    /// <param name="storageType">The <c>Graph:Storage:Type</c> it provides.</param>
    public string ProvidesDatabase(string storageType) => Get("setup.packages.providesDatabase", storageType);

    // ── Step: database ─────────────────────────────────────────────────────────────────────────
    /// <summary>The database step heading.</summary>
    public string StorageHeading => Get("setup.storage.heading");
    /// <summary>The database step explanation.</summary>
    public string StorageHelp => Get("setup.storage.help");
    /// <summary>The connection-string field label.</summary>
    public string ConnectionStringLabel => Get("setup.storage.connectionString");
    /// <summary>The base-path field label.</summary>
    public string BasePathLabel => Get("setup.storage.basePath");

    // ── Step: sign-in ──────────────────────────────────────────────────────────────────────────
    /// <summary>The sign-in step heading.</summary>
    public string SignInHeading => Get("setup.signin.heading");
    /// <summary>The sign-in step explanation.</summary>
    public string SignInHelp => Get("setup.signin.help");
    /// <summary>The developer-login switch label.</summary>
    public string DevLoginLabel => Get("setup.signin.devLogin");
    /// <summary>The developer-login warning about public instances.</summary>
    public string DevLoginHelp => Get("setup.signin.devLogin.help");
    /// <summary>The platform-admin ids field label.</summary>
    public string DevAdminsLabel => Get("setup.signin.devAdmins");
    /// <summary>The client-id field label.</summary>
    public string ClientIdLabel => Get("setup.signin.clientId");
    /// <summary>The client-secret field label.</summary>
    public string ClientSecretLabel => Get("setup.signin.clientSecret");
    /// <summary>The tenant field label.</summary>
    public string TenantLabel => Get("setup.signin.tenant");
    /// <summary>Marks a route the host's own configuration already answers.</summary>
    public string AlreadyConfigured => Get("setup.signin.alreadyConfigured");

    // ── Step: models ───────────────────────────────────────────────────────────────────────────
    /// <summary>The models step heading.</summary>
    public string AiHeading => Get("setup.ai.heading");
    /// <summary>The models step explanation.</summary>
    public string AiHelp => Get("setup.ai.help");
    /// <summary>The API-key field label.</summary>
    public string ApiKeyLabel => Get("setup.ai.apiKey");
    /// <summary>The endpoint field label.</summary>
    public string EndpointLabel => Get("setup.ai.endpoint");
    /// <summary>The embeddings sub-heading.</summary>
    public string EmbeddingHeading => Get("setup.ai.embedding.heading");
    /// <summary>Why the embeddings endpoint matters.</summary>
    public string EmbeddingHelp => Get("setup.ai.embedding.help");
    /// <summary>The embedding-model field label.</summary>
    public string EmbeddingModelLabel => Get("setup.ai.embedding.model");

    // ── Step: modules ──────────────────────────────────────────────────────────────────────────
    /// <summary>The modules step heading.</summary>
    public string ModulesHeading => Get("setup.modules.heading");
    /// <summary>The modules step explanation.</summary>
    public string ModulesHelp => Get("setup.modules.help");
    /// <summary>The provisioned-packages field label.</summary>
    public string PackagesLabel => Get("setup.modules.packages");

    // ── Refusals ───────────────────────────────────────────────────────────────────────────────
    /// <summary>No backend was chosen.</summary>
    public string ProblemNoStorage => Get("setup.problem.noStorage");
    /// <summary>The chosen backend is not one this image ships.</summary>
    /// <param name="chosen">What was submitted.</param>
    /// <param name="available">The keys this image registered.</param>
    public string ProblemUnknownStorage(string chosen, string available) =>
        Get("setup.problem.unknownStorage", chosen, available);
    /// <summary>The backend needs a connection string.</summary>
    /// <param name="backend">The backend's display name.</param>
    public string ProblemNoConnectionString(string backend) =>
        Get("setup.problem.noConnectionString", backend);
    /// <summary>The backend needs a base path.</summary>
    /// <param name="backend">The backend's display name.</param>
    public string ProblemNoBasePath(string backend) => Get("setup.problem.noBasePath", backend);
    /// <summary>No sign-in route at all was chosen.</summary>
    public string ProblemNoSignIn => Get("setup.problem.noSignIn");
    /// <summary>A sign-in route this image cannot serve.</summary>
    /// <param name="name">The submitted route name.</param>
    public string ProblemUnknownSignIn(string name) => Get("setup.problem.unknownSignIn", name);
    /// <summary>A sign-in route was given a client id but no secret.</summary>
    /// <param name="provider">The provider's display name.</param>
    public string ProblemNoSignInSecret(string provider) =>
        Get("setup.problem.noSignInSecret", provider);
    /// <summary>A model provider this image cannot call.</summary>
    /// <param name="name">The submitted provider name.</param>
    public string ProblemUnknownAiProvider(string name) =>
        Get("setup.problem.unknownAiProvider", name);
    /// <summary>A model provider that requires a key was given none.</summary>
    /// <param name="provider">The provider's display name.</param>
    public string ProblemNoApiKey(string provider) => Get("setup.problem.noApiKey", provider);
    /// <summary>A module entry this image does not ship.</summary>
    /// <param name="entry">The submitted entry.</param>
    public string ProblemUnknownModule(string entry) => Get("setup.problem.unknownModule", entry);
    /// <summary>A secret could not be encrypted because the install has no master key.</summary>
    /// <param name="what">What the secret belongs to.</param>
    public string ProblemNoMasterKey(string what) => Get("setup.problem.noMasterKey", what);

    /// <summary>No instance name was given.</summary>
    public string ProblemNoInstanceName => Get("setup.problem.noInstanceName");
    /// <summary>The instance id does not fit the registry's alphabet.</summary>
    /// <param name="id">What was submitted.</param>
    public string ProblemBadInstanceId(string id) => Get("setup.problem.badInstanceId", id);
    /// <summary>The registry address is not a URL.</summary>
    /// <param name="url">What was submitted.</param>
    public string ProblemBadRegistry(string url) => Get("setup.problem.badRegistry", url);
    /// <summary>Registered, but the issued key could not be persisted — and the id is now claimed.</summary>
    /// <param name="id">The id that was claimed.</param>
    public string ProblemKeyUnstorable(string id) => Get("setup.problem.keyUnstorable", id);

    // ── Warnings ───────────────────────────────────────────────────────────────────────────────
    /// <summary>The developer login is on with no platform admin named.</summary>
    public string WarnDevLoginWithoutAdmins => Get("setup.warn.devLoginWithoutAdmins");
    /// <summary>No embeddings endpoint — vector search will be dark.</summary>
    public string WarnNoEmbeddings => Get("setup.warn.noEmbeddings");
}
