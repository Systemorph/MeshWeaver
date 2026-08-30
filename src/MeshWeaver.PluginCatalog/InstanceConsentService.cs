using System.Reactive;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The platform half of "an installation asks before it registers": the texts a platform admin
/// must accept (the privacy statement at <see cref="PrivacyPath"/>, the platform terms at
/// <see cref="TermsPath"/>), the <see cref="InstanceConsent"/> record that gates the open
/// registration, and the live views an APP needs to show where the installation stands —
/// consent, credential (with the plan the registry put it on), and the catalogue the registry
/// serves it. The app itself is the Hosting plugin's; nothing here renders.
///
/// <para>Texts are read as System (the Admin partition is not readable by every principal) and
/// hashed for the record exactly as shown, so the audit trail names the version that was
/// accepted. The consent WRITE runs under the caller's own identity: only a global admin can
/// write the Admin partition, and that is precisely what "consent on the deployment's behalf"
/// means — nothing here impersonates for it.</para>
/// </summary>
public sealed class InstanceConsentService(IMessageHub hub, ILogger<InstanceConsentService> logger)
{
    /// <summary>The privacy statement's node (a Markdown node the portal serves at <c>/privacy</c>).</summary>
    public const string PrivacyPath = "Admin/Privacy";

    /// <summary>The platform terms' node — a Markdown node created here with
    /// <see cref="DefaultTerms"/> when absent, editable like any Markdown node.</summary>
    public const string TermsPath = "Admin/Terms";

    private const string TermsId = "Terms";
    private const string AdminPartition = "Admin";

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Shown when no privacy statement has been published on this installation yet.</summary>
    public const string MissingPrivacyStatement = """
        # Privacy Statement

        No privacy statement has been published on this installation yet. A platform admin publishes
        one under Settings ▸ Privacy; it is served publicly at `/privacy`.
        """;

    /// <summary>The default platform terms a fresh installation shows. Generic on purpose: they
    /// describe what registering at a registry means for the deployment and leave commercial terms
    /// to the plan the registry puts the instance on.</summary>
    public const string DefaultTerms = """
        # Platform Terms

        By registering this installation at a MeshWeaver plugin registry you agree to the
        following on behalf of the organisation operating it.

        ## What registration is

        Registration gives this installation an identity at the registry — an instance id, a
        credential, and a plan. The plan determines which packages the registry serves to it. A
        new installation starts on the free plan; the registry's platform administrators may
        change the plan.

        ## What is exchanged

        The installation sends its instance id, display name and public URL to the registry, and
        thereafter presents a short-lived signed token on every request. The registry records when
        the installation last authenticated and which packages it pulled. No user data of this
        installation is sent to the registry.

        ## Packages

        Packages served by the registry are governed by the licence each package declares; the
        Store shows it before a package is installed, and a package that requires acceptance is
        not installed until a user accepts. Packages above the installation's plan are not served.

        ## Withdrawal

        A platform administrator of this installation may withdraw this consent in the Hosting
        app; the installation then stops registering, and deleting the stored credential stops it
        authenticating at the registry.
        """;

    /// <summary>The registry this installation registers at and the id it claims, from
    /// configuration — null when no registry or no instance id is configured.</summary>
    public (PluginRegistryReference Registry, string InstanceId, bool Keyed)? Target()
    {
        var options = hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions();
        var registry = RegistryTokenResolver.WithLegacyTokens(options, options.EffectiveRegistries).FirstOrDefault();
        var instanceId = options.InstanceId?.Trim() ?? "";
        if (registry is null || instanceId.Length == 0)
            return null;
        return (registry, instanceId, !string.IsNullOrWhiteSpace(options.BootstrapKey));
    }

    /// <summary>Both texts, as shown — the privacy statement (or <see cref="MissingPrivacyStatement"/>)
    /// and the terms (created with <see cref="DefaultTerms"/> on first use). Emits once.</summary>
    public IObservable<ConsentTexts> Texts()
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        var workspace = hub.GetWorkspace();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        IObservable<string> Read(string path, string fallback) => workspace
            .GetQuery($"consent-text|{path}", $"path:{path} nodeType:Markdown")
            .Take(1)
            .Timeout(ReadTimeout)
            .SelectMany(nodes => nodes.Any()
                ? workspace.GetMeshNodeStream(path)
                    .Where(node => node is not null)
                    .Take(1)
                    .Timeout(ReadTimeout)
                    .Select(node => Markdown(node!.Content) ?? fallback)
                : Observable.Return(fallback));

        IObservable<string> EnsureTerms() => workspace
            .GetQuery($"consent-text|{TermsPath}", $"path:{TermsPath} nodeType:Markdown")
            .Take(1)
            .Timeout(ReadTimeout)
            .SelectMany(nodes => nodes.Any()
                ? Observable.Return(TermsPath)
                : meshService.CreateNode(new MeshNode(TermsId, AdminPartition)
                    {
                        NodeType = "Markdown",
                        Name = "Platform Terms",
                        State = MeshNodeState.Active,
                        Content = new { content = DefaultTerms },
                    })
                    .Select(_ => TermsPath)
                    .Catch<string, Exception>(ex => IsAlreadyExists(ex)
                        ? Observable.Return(TermsPath)
                        : Observable.Throw<string>(ex)));

        // One sealed System scope around the reads (#1790): the subscriber is the admin's circuit.
        return access.RunAsSystem(() => EnsureTerms()
            .SelectMany(_ => Observable.CombineLatest(
                Read(PrivacyPath, MissingPrivacyStatement),
                Read(TermsPath, DefaultTerms),
                (privacy, terms) => new ConsentTexts(privacy, terms))
                .Take(1)));
    }

    /// <summary>The consent record, or null — LIVE: the app's form gives way the moment it is
    /// written and returns the moment it is withdrawn. Existence through a query (a point read of
    /// a maybe-absent node opens the storm breaker, #2229), content off the node stream.</summary>
    public IObservable<InstanceConsent?> Observe()
    {
        var workspace = hub.GetWorkspace();
        var path = MeshWeaverInstanceNodeType.ConsentPath;
        return workspace
            .GetQuery($"consent|{path}", $"path:{path} nodeType:{MeshWeaverInstanceNodeType.ConsentNodeType}")
            .Select(rows => rows.Any())
            .DistinctUntilChanged()
            .Select(present => present
                ? workspace.GetMeshNodeStream(path)
                    .Where(node => node is not null)
                    .Select(node => node!.ContentAs<InstanceConsent>(hub.JsonSerializerOptions))
                : Observable.Return<InstanceConsent?>(null))
            .Switch();
    }

    /// <summary>The stored registry credential for <paramref name="registryUrl"/>, or null — LIVE,
    /// so a view flips to "registered" when the waiting registration completes after consent.
    /// Carries the plan the registry echoed.</summary>
    public IObservable<PluginRegistryCredential?> Credential(string registryUrl)
    {
        var workspace = hub.GetWorkspace();
        var path = PluginRegistryCredentials.Path(registryUrl);
        return workspace
            .GetQuery($"credential|{path}", $"path:{path} nodeType:{PluginRegistryCredentials.NodeType}")
            .Select(rows => rows.Any())
            .DistinctUntilChanged()
            .Select(present => present
                ? workspace.GetMeshNodeStream(path)
                    .Where(node => node is not null)
                    .Select(node => node!.ContentAs<PluginRegistryCredential>(hub.JsonSerializerOptions))
                : Observable.Return<PluginRegistryCredential?>(null))
            .Switch();
    }

    /// <summary>
    /// Records the consent: the two texts as shown (hashed), the instance and registry, the
    /// accepting principal from the CALLER's context. Create, never create-or-update — a second
    /// consent does not overwrite the first. Runs under the caller's identity: writing the Admin
    /// partition is what only a global admin may do, which is the whole meaning of consenting on
    /// the deployment's behalf. Cold — subscribe to write.
    /// </summary>
    public IObservable<MeshNode> Give(ConsentTexts texts, string instanceId, string registryUrl)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return Observable.Throw<MeshNode>(new ArgumentException("An instance id is required.", nameof(instanceId)));
        var context = hub.ServiceProvider.GetService<AccessService>()?.Context;
        if (context is null || string.IsNullOrWhiteSpace(context.ObjectId))
            return Observable.Throw<MeshNode>(new InvalidOperationException(
                "Consent must be given by a signed-in platform admin — no principal is on the current context."));

        var consent = new InstanceConsent
        {
            InstanceId = instanceId.Trim(),
            RegistryUrl = (registryUrl ?? "").TrimEnd('/'),
            PrivacyStatementHash = Sha256(texts.Privacy),
            TermsHash = Sha256(texts.Terms),
            AcceptedAt = DateTimeOffset.UtcNow,
            AcceptedByUserId = context.ObjectId,
            AcceptedByName = context.Name ?? "",
            AcceptedByEmail = context.Email ?? "",
        };
        var node = new MeshNode(MeshWeaverInstanceNodeType.ConsentId, MeshWeaverInstanceNodeType.ConsentNamespace)
        {
            Name = "Instance consent",
            NodeType = MeshWeaverInstanceNodeType.ConsentNodeType,
            State = MeshNodeState.Active,
            Content = consent,
        };
        return hub.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(node)
            .Do(_ => logger.LogInformation(
                "Instance consent recorded for '{InstanceId}' at {Registry} by {User}",
                consent.InstanceId, consent.RegistryUrl, consent.AcceptedByEmail));
    }

    /// <summary>Withdraws the consent by deleting the record — the installation stops registering.
    /// The stored credential is left in place; deleting it is the separate, louder step that stops
    /// an already-registered installation from authenticating. Cold.</summary>
    public IObservable<Unit> Withdraw() =>
        hub.ServiceProvider.GetRequiredService<IMeshService>()
            .DeleteNode(MeshWeaverInstanceNodeType.ConsentPath)
            .Select(_ => Unit.Default)
            .Do(_ => logger.LogInformation("Instance consent withdrawn"));

    /// <summary>
    /// The catalogue the registry serves THIS installation, listed through the same token exchange
    /// every catalogue call uses — so the count an app shows is the count the Store sees: the
    /// packages the instance's plan covers, from the sources it is granted. Emits once.
    /// </summary>
    public IObservable<IReadOnlyList<PackageManifest>> Catalogue(PluginRegistryReference registry)
    {
        var resolver = hub.ServiceProvider.GetRequiredService<RegistryTokenResolver>();
        return resolver.ResolveToken(registry)
            .SelectMany(token => new RegistryPackageSource(hub, registry.Url, token).ListPackages("HEAD"))
            .Take(1);
    }

    private string? Markdown(object? content) => content switch
    {
        string s => s,
        JsonElement je when je.ValueKind == JsonValueKind.Object
                            && je.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            => c.GetString(),
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        null => null,
        _ => JsonSerializer.SerializeToElement(content, hub.JsonSerializerOptions) is var element
             && element.ValueKind == JsonValueKind.Object
             && element.TryGetProperty("content", out var typed) && typed.ValueKind == JsonValueKind.String
            ? typed.GetString()
            : null,
    };

    private static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();
}

/// <summary>The two texts a consent is given for, as shown.</summary>
/// <param name="Privacy">The privacy statement markdown.</param>
/// <param name="Terms">The platform terms markdown.</param>
public sealed record ConsentTexts(string Privacy, string Terms);
