using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// What the operator answered, in the shape the form submits it. One flat record so that composing
/// a manifest is a PURE function of the answers plus the image's catalogs — which is what lets
/// every rule below be asserted without a browser, a database or a host.
/// </summary>
public sealed record SetupAnswers
{
    /// <summary>The chosen <c>Graph:Storage:Type</c>.</summary>
    public string StorageType { get; init; } = "";

    /// <summary>The connection string, for a backend that needs one.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>The base path, for a backend rooted at a directory.</summary>
    public string? BasePath { get; init; }

    /// <summary>Whether the developer login is on.</summary>
    public bool EnableDevLogin { get; init; }

    /// <summary>Comma-separated ids granted platform admin through the developer login.</summary>
    public string? DevAdminUsers { get; init; }

    /// <summary>External sign-in providers, keyed by scheme name.</summary>
    public ImmutableList<SetupSignInAnswer> SignIn { get; init; } = [];

    /// <summary>Model providers and their keys.</summary>
    public ImmutableList<SetupAiAnswer> Ai { get; init; } = [];

    /// <summary>The embeddings endpoint. Blank means none — and vector search stays dark.</summary>
    public string? EmbeddingEndpoint { get; init; }

    /// <summary>The embedding model.</summary>
    public string? EmbeddingModel { get; init; }

    /// <summary>Module assemblies to boot.</summary>
    public ImmutableList<string> BootModules { get; init; } = [];

    /// <summary>Packages to provision into the mesh at first boot.</summary>
    public ImmutableList<string> ProvisionPackages { get; init; } = [];

    /// <summary>Who is answering, for the manifest's provenance field.</summary>
    public string? SetUpBy { get; init; }
}

/// <summary>One external sign-in provider's answers.</summary>
/// <param name="Name">The scheme name.</param>
/// <param name="ClientId">The OAuth client id. Blank turns the provider off.</param>
/// <param name="TenantId">The tenant, for the provider that has one.</param>
/// <param name="ClientSecret">The client secret, in the clear as the form submitted it. Encrypted
/// by <see cref="SetupComposition"/> before it reaches the manifest.</param>
public sealed record SetupSignInAnswer(
    string Name, string? ClientId, string? TenantId, string? ClientSecret);

/// <summary>One model provider's answers.</summary>
/// <param name="Name">The provider name.</param>
/// <param name="ApiKey">The API key, in the clear as the form submitted it.</param>
/// <param name="Endpoint">The endpoint, when the operator chooses it.</param>
public sealed record SetupAiAnswer(string Name, string? ApiKey, string? Endpoint);

/// <summary>The composed outcome: a manifest to write, or the problems that stopped it.</summary>
/// <param name="Manifest">The manifest to write, or null when <paramref name="Problems"/> is not empty.</param>
/// <param name="Problems">Everything wrong with the answers, all of it — never just the first.</param>
/// <param name="Warnings">Things that are legal but will bite later, shown but not blocking.</param>
public sealed record SetupPlan(
    InstanceManifest? Manifest,
    ImmutableList<string> Problems,
    ImmutableList<string> Warnings);

/// <summary>
/// Turns the operator's answers into the instance manifest — the single place the first-run wizard
/// decides what a new instance IS.
///
/// <para>🚨 <b>Pure, and it refuses.</b> Nothing here writes a file, reads configuration or touches
/// a hub. Every rule is a validation the operator can still fix while the mistake is cheap: after
/// this the answer is durable, the process restarts, and a wrong one surfaces as a boot failure
/// whose message names a symptom rather than the choice that caused it.</para>
///
/// <para><b>It writes the SAME artifact the fleet route writes</b> —
/// <see cref="InstanceManifest"/> — which is the property that keeps the interactive and fleet
/// provisioning paths from drifting until only one of them works.</para>
/// </summary>
public static class SetupComposition
{
    /// <summary>
    /// Composes the manifest, or the reasons it cannot be composed.
    /// </summary>
    /// <param name="answers">What the operator submitted. Never null.</param>
    /// <param name="catalog">What the image offers, so an answer naming something it does not ship
    /// is refused HERE rather than at the next boot.</param>
    /// <param name="storage">The keyed storage backends this image registered.</param>
    /// <param name="protector">Encrypts the secrets. Null means no master key could be provisioned,
    /// which is refused outright: storing a live credential in plaintext is the 2026-08-24 leak, and
    /// silently DROPPING it would produce a provider that renders a button and fails at the token
    /// exchange.</param>
    /// <param name="strings">The viewer-locale strings every refusal is phrased in — the wizard
    /// runs pre-mesh, so the locale is passed in rather than resolved from an ambient culture.</param>
    /// <param name="now">The completion timestamp, injected so the result is assertable.</param>
    public static SetupPlan Compose(
        SetupAnswers answers,
        SetupCatalog catalog,
        StorageBackendCatalog storage,
        IProviderKeyProtector? protector,
        SetupStrings strings,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(strings);

        var problems = ImmutableList.CreateBuilder<string>();
        var warnings = ImmutableList.CreateBuilder<string>();

        var storageOption = catalog.Storage.FirstOrDefault(
            o => string.Equals(o.Type, answers.StorageType?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(answers.StorageType))
            problems.Add(strings.ProblemNoStorage);
        else if (storageOption is null || !storage.Offers(answers.StorageType))
            // Naming what IS available turns a dead end into a next step. The list is short and
            // contains no secrets — it is the set of keys the image compiled in.
            problems.Add(strings.ProblemUnknownStorage(
                answers.StorageType.Trim(), string.Join(", ", storage.Types)));

        if (storageOption is { NeedsConnectionString: true }
            && string.IsNullOrWhiteSpace(answers.ConnectionString))
            problems.Add(strings.ProblemNoConnectionString(storageOption.DisplayName));

        if (storageOption is { NeedsBasePath: true } && string.IsNullOrWhiteSpace(answers.BasePath))
            problems.Add(strings.ProblemNoBasePath(storageOption.DisplayName));

        // 🚨 A sign-in answer with NO route at all is an instance nobody can enter. It is a
        // recoverable state (the manifest can be edited on disk) but not one to walk into
        // unknowingly, so it is refused rather than warned about.
        var chosenSignIn = answers.SignIn
            .Where(a => !string.IsNullOrWhiteSpace(a.ClientId))
            .ToImmutableList();
        var configuredElsewhere = catalog.SignIn.Any(o => o.AlreadyConfigured);
        if (!answers.EnableDevLogin && chosenSignIn.IsEmpty && !configuredElsewhere)
            problems.Add(strings.ProblemNoSignIn);

        if (answers.EnableDevLogin && string.IsNullOrWhiteSpace(answers.DevAdminUsers))
            // A PAIR: the login without the admin list is a portal you can enter and administer
            // nothing on. Legal — someone else may hold Auth:GlobalAdmins — so a warning.
            warnings.Add(strings.WarnDevLoginWithoutAdmins);

        var signInProviders = ImmutableList.CreateBuilder<InstanceSignInProvider>();
        foreach (var answer in chosenSignIn)
        {
            var option = catalog.SignIn.FirstOrDefault(
                o => string.Equals(o.Name, answer.Name, StringComparison.OrdinalIgnoreCase));
            if (option is null)
            {
                problems.Add(strings.ProblemUnknownSignIn(answer.Name));
                continue;
            }

            // A scheme turned on with no secret renders its button and fails at the token
            // exchange — strictly worse than an absent provider, so it is refused.
            if (string.IsNullOrWhiteSpace(answer.ClientSecret))
            {
                problems.Add(strings.ProblemNoSignInSecret(option.DisplayName));
                continue;
            }

            if (Protect(answer.ClientSecret, protector, option.DisplayName, strings, problems) is not { } secret)
                continue;

            signInProviders.Add(new InstanceSignInProvider
            {
                Name = option.Name,
                Section = option.Section,
                ClientId = answer.ClientId!.Trim(),
                TenantId = option.HasTenant ? answer.TenantId?.Trim() : null,
                ClientSecret = secret,
            });
        }

        var aiProviders = ImmutableList.CreateBuilder<InstanceAiProvider>();
        foreach (var answer in answers.Ai)
        {
            var option = catalog.Ai.FirstOrDefault(
                o => string.Equals(o.Name, answer.Name, StringComparison.OrdinalIgnoreCase));
            if (option is null)
            {
                problems.Add(strings.ProblemUnknownAiProvider(answer.Name));
                continue;
            }

            var hasKey = !string.IsNullOrWhiteSpace(answer.ApiKey);
            var hasEndpoint = !string.IsNullOrWhiteSpace(answer.Endpoint);

            // 🚨 "Chosen" is decided from what was SUBMITTED, before any default is substituted.
            // Applying option.DefaultEndpoint first made every provider that has one look chosen —
            // an untouched row became a configured provider, which is the same defect as rendering
            // the default into the field's value, one layer down. Nothing supplied ⇒ not chosen.
            if (!hasKey && !hasEndpoint)
                continue;

            // Only now: a row the operator DID answer inherits the provider's own endpoint when
            // they left that field alone.
            var endpoint = hasEndpoint ? answer.Endpoint!.Trim() : option.DefaultEndpoint;

            if (option.RequiresApiKey && !hasKey)
            {
                problems.Add(strings.ProblemNoApiKey(option.DisplayName));
                continue;
            }

            string? protectedKey = null;
            if (hasKey && Protect(answer.ApiKey, protector, option.DisplayName, strings, problems) is { } k)
                protectedKey = k;
            else if (hasKey)
                continue;

            aiProviders.Add(new InstanceAiProvider
            {
                Name = option.Name,
                Section = option.Section,
                ApiKey = protectedKey,
                Endpoint = endpoint,
            });
        }

        var embeddings = string.IsNullOrWhiteSpace(answers.EmbeddingEndpoint)
            ? null
            : new InstanceEmbeddingsSelection
            {
                Endpoint = answers.EmbeddingEndpoint.Trim(),
                Model = string.IsNullOrWhiteSpace(answers.EmbeddingModel)
                    ? InstanceEmbeddingsSelection.DefaultModel
                    : answers.EmbeddingModel.Trim(),
            };

        // 🚨 The warning that is the whole reason this step exists. Without an embedder every write
        // stores a NULL embedding and the vector provider contributes nothing — search silently
        // degrades to lexical, with no error, no log line and no way for the operator to tell.
        if (embeddings is null)
            warnings.Add(strings.WarnNoEmbeddings);

        var unknownModules = answers.BootModules
            .Where(entry => !catalog.Modules.Any(
                m => string.Equals(m.Entry, entry, StringComparison.OrdinalIgnoreCase)))
            .ToImmutableList();
        foreach (var entry in unknownModules)
            problems.Add(strings.ProblemUnknownModule(entry));

        if (problems.Count > 0)
            return new SetupPlan(null, problems.ToImmutable(), warnings.ToImmutable());

        var manifest = new InstanceManifest
        {
            State = InstanceSetupState.Complete,
            Storage = new InstanceStorageSelection
            {
                Type = storageOption!.Type,
                ConnectionString = Blank(answers.ConnectionString),
                BasePath = Blank(answers.BasePath),
            },
            BootModules = answers.BootModules,
            ProvisionPackages = answers.ProvisionPackages.IsEmpty
                ? InstanceSetupDefaults.ProvisionPackages
                : answers.ProvisionPackages,
            UserPreInstallPackages = InstanceSetupDefaults.UserPreInstallPackages,
            SignIn = new InstanceSignInSelection
            {
                EnableDevLogin = answers.EnableDevLogin,
                DevAdminUsers = Blank(answers.DevAdminUsers),
                Providers = signInProviders.ToImmutable(),
            },
            Ai = new InstanceAiSelection
            {
                Providers = aiProviders.ToImmutable(),
                Embeddings = embeddings,
            },
            SetUpBy = Blank(answers.SetUpBy),
            SetUpAt = now,
        };

        return new SetupPlan(manifest, [], warnings.ToImmutable());
    }

    /// <summary>
    /// Encrypts a secret, or records why it could not be. Never returns the plaintext: a manifest
    /// carrying an unprotected credential is the defect <c>ProviderKeyProtector</c> throws to
    /// prevent, and the setup surface has a master key by construction — so reaching this refusal
    /// means the install could not provision one, which the operator must be told.
    /// </summary>
    private static string? Protect(
        string? plaintext, IProviderKeyProtector? protector, string what,
        SetupStrings strings, ImmutableList<string>.Builder problems)
    {
        if (protector is null)
        {
            problems.Add(strings.ProblemNoMasterKey(what));
            return null;
        }
        try
        {
            return protector.Protect(plaintext?.Trim());
        }
        catch (InvalidOperationException)
        {
            // The protector's own refusal, which already names the setting. Never echo the value.
            problems.Add(strings.ProblemNoMasterKey(what));
            return null;
        }
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
