using System.Collections.Immutable;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// What the first-run wizard may OFFER — the image's actual capabilities, never a wish list.
///
/// <para>🚨 <b>Every list here is discovered, and that is the load-bearing property.</b> A wizard
/// that offered a hard-coded menu would let an operator record a backend the image cannot resolve
/// (<c>Unknown storage type: 'Cosmos'</c> at the next boot, after the answer is durable), or a
/// sign-in provider whose handler was never registered — the operator turns it on, the host
/// registers no scheme, and <c>/auth/login?provider=X</c> answers <c>400 Unknown provider</c> with
/// every value correct. Both failures land a whole restart away from the choice that caused them.
/// So: storage comes from the keyed <c>IStorageAdapterFactory</c> registrations, sign-in from
/// <c>SignInProviderCatalog</c>, models from the registered
/// <c>LanguageModelCatalogSource</c>s.</para>
/// </summary>
/// <param name="Storage">The backends this image can actually open.</param>
/// <param name="SignIn">The login routes this image can actually serve.</param>
/// <param name="Ai">The model providers this image can actually call.</param>
/// <param name="Modules">The module assemblies this image ships and can boot.</param>
///
/// <para>🚨 <see cref="Packages"/>, <see cref="Identity"/> and <see cref="RegistryProblem"/> are
/// INIT PROPERTIES, deliberately not primary-constructor parameters. Adding a parameter to a
/// record's primary constructor — even with a default — replaces the emitted constructor, so every
/// assembly already compiled against the old arity calls a method that no longer exists. A module
/// and the platform it loads into must agree on that signature EXACTLY and no image can serve a
/// mixed set, which is what the `Public surface (binary compatibility)` gate refuses. An init
/// property is binary-ADDITIVE: old callers keep working, new callers use an object initialiser or
/// `with { … }`. Same reasoning, same shape as <c>InstanceRegistrationPayloads.Response.Plan</c>.</para>
public sealed record SetupCatalog(
    ImmutableList<SetupStorageOption> Storage,
    ImmutableList<SetupSignInOption> SignIn,
    ImmutableList<SetupAiOption> Ai,
    ImmutableList<SetupModuleOption> Modules)
{
    /// <summary>What the REGISTRY says this instance is entitled to, once it has registered. Empty
    /// before registration, and empty is a legitimate answer afterwards — a plan may grant nothing.
    /// Never null.</summary>
    public ImmutableList<SetupPackageOption> Packages { get; init; } = [];

    /// <summary>Who this instance registered as, or null before it has.</summary>
    public MeshWeaver.Mesh.InstanceIdentitySelection? Identity { get; init; }

    /// <summary>Why the catalog could not be listed, when it could not be. Shown to the operator
    /// rather than swallowed: an instance that registered but cannot see its plan is a state worth
    /// naming, not a silently empty list.</summary>
    public string? RegistryProblem { get; init; }

    /// <summary>An image that offers nothing — the shape a host with no contributor answers with,
    /// which the surface renders as an explicit "this image ships no options" rather than as an
    /// empty form somebody could submit.</summary>
    public static SetupCatalog Empty { get; } = new([], [], [], []);
}

/// <summary>
/// One package the registry says this instance may install.
///
/// <para>🚨 <see cref="StorageType"/> is what lets a package answer the DATABASE question. It is
/// the <c>Graph:Storage:Type</c> the package's boot pack registers, and it is only offerable when
/// the image can already load that backend — see <see cref="SetupStorageOption"/>.</para>
/// </summary>
/// <param name="Id">The package id, as the catalog serves it.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Description">What it is.</param>
/// <param name="StorageType">The storage backend it provides, or null.</param>
/// <param name="PreSelected">Whether a "next, next, finish" install provisions it.</param>
public sealed record SetupPackageOption(
    string Id,
    string Name,
    string? Description = null,
    string? StorageType = null,
    bool PreSelected = false);

/// <summary>One storage backend the image can open, as a keyed
/// <c>IStorageAdapterFactory</c> registration named it.</summary>
/// <param name="Type">The key — exactly the value <c>Graph:Storage:Type</c> takes.</param>
/// <param name="DisplayName">The label. Falls back to <paramref name="Type"/>.</param>
/// <param name="NeedsConnectionString">Whether the backend cannot open without one.</param>
/// <param name="NeedsBasePath">Whether the backend is rooted at a directory instead.</param>
/// <param name="ConnectionStringHint">An example, shown as the field's placeholder. Never a real
/// credential — the hints are literals in code, seen by whoever is setting the instance up.</param>
public sealed record SetupStorageOption(
    string Type,
    string DisplayName,
    bool NeedsConnectionString = false,
    bool NeedsBasePath = false,
    string? ConnectionStringHint = null)
{
    /// <summary>The registry package that provides this backend, when one does. Null for a backend
    /// compiled into the image.
    ///
    /// <para>🚨 A package is only listed here when the IMAGE can already open the backend — i.e. its
    /// keyed factory is registered. A storage module the image lacks cannot be offered, because
    /// landing it would have to happen BEFORE persistence selection reads <c>Graph:Storage</c>, and
    /// package provisioning runs after the mesh is up. Offering one anyway would record a backend
    /// that never resolves and fail at the NEXT boot, with the wizard gone.</para>
    ///
    /// <para>🚨 An INIT property, not a sixth constructor parameter — see the note on
    /// <see cref="SetupCatalog"/>: adding a positional parameter would be a binary break for every
    /// assembly compiled against the 5-arity constructor.</para></summary>
    public string? PackageId { get; init; }
}

/// <summary>One sign-in route the image can serve.</summary>
/// <param name="Name">The scheme name — the value <c>/auth/login?provider=</c> takes.</param>
/// <param name="DisplayName">The label on the sign-in button.</param>
/// <param name="Section">The configuration section its keys live under, in colon form. NOT derived
/// from the name: GitHub's is <c>GitHub:OAuth</c>.</param>
/// <param name="IsSwitch">True for the developer login — a boolean with no credential at all.</param>
/// <param name="HasTenant">True for the one provider that takes a tenant.</param>
/// <param name="AlreadyConfigured">Whether the host's own configuration already answers this route,
/// in which case the wizard shows it as configured and does not offer to overwrite it — the
/// manifest is projected UNDER configuration and could not win anyway.</param>
public sealed record SetupSignInOption(
    string Name,
    string DisplayName,
    string Section,
    bool IsSwitch = false,
    bool HasTenant = false,
    bool AlreadyConfigured = false);

/// <summary>One model provider the image can call.</summary>
/// <param name="Name">The provider name, e.g. <c>Anthropic</c>.</param>
/// <param name="DisplayName">The label.</param>
/// <param name="Section">The configuration section the provider package binds.</param>
/// <param name="RequiresApiKey">False for the providers that have none — a local Ollama, the
/// co-hosted Claude Code CLI, Copilot — so the wizard does not demand a key that does not exist.</param>
/// <param name="DefaultEndpoint">Pre-filled endpoint, when the provider has a well-known one.</param>
/// <param name="TakesEndpoint">Whether the endpoint is the operator's to choose (a self-hosted,
/// OpenAI-compatible server) rather than the provider's own fixed address.</param>
public sealed record SetupAiOption(
    string Name,
    string DisplayName,
    string Section,
    bool RequiresApiKey = true,
    string? DefaultEndpoint = null,
    bool TakesEndpoint = false);

/// <summary>One module assembly the image ships.</summary>
/// <param name="Entry">The <c>Modules:Assemblies</c> entry — a file name, e.g.
/// <c>MeshWeaver.Hosting.Grpc.dll</c>.</param>
/// <param name="DisplayName">The label.</param>
/// <param name="Description">What turning it on gets you.</param>
/// <param name="PreSelected">Whether a "next, next, finish" install boots with it on.</param>
public sealed record SetupModuleOption(
    string Entry,
    string DisplayName,
    string? Description = null,
    bool PreSelected = false);

/// <summary>
/// Supplies the choices the wizard offers. Implemented where the catalogs live — the portal
/// composition — because core can enumerate keyed storage factories but not
/// <c>SignInProviderCatalog</c> or the model catalog sources, which are compiled in the host repo.
///
/// <para>Resolved OPTIONALLY: a host that registers none gets <see cref="SetupCatalog.Empty"/> and
/// a surface that says so, rather than a null reference during boot of an instance that is already
/// in trouble.</para>
/// </summary>
public interface ISetupCatalogProvider
{
    /// <summary>The choices this image can offer right now.</summary>
    SetupCatalog Describe();
}
