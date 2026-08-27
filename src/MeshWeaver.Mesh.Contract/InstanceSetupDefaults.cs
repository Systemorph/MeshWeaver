using System.Collections.Immutable;

namespace MeshWeaver.Mesh;

/// <summary>
/// What a new instance is offered BEFORE anyone answers a question (#2550) — the setup wizard's
/// pre-filled profile, derived from what a working first-party deployment actually runs rather
/// than from a wish list.
///
/// <para>🚨 <b>Defaults, never a policy.</b> Every value here is something the operator can change
/// in the wizard; the point is that "next, next, finish" produces an instance that WORKS, instead
/// of an empty mesh whose owner has to already know which seven modules make a portal render.</para>
/// </summary>
public static class InstanceSetupDefaults
{
    /// <summary>
    /// The default storage backend. <c>PostgreSql</c> because that is what every deployed
    /// installation runs — the file-system backend exists for local development, and offering it
    /// as the default is how someone ends up running a portal on container-ephemeral disk.
    ///
    /// <para>🚨 Offered, not imposed: the wizard lists what the IMAGE actually ships (the keyed
    /// <c>IStorageAdapterFactory</c> registrations) and pre-selects this one only when present.</para>
    /// </summary>
    public const string StorageType = "PostgreSql";

    /// <summary>
    /// The source a fresh instance provisions from. Matches the first-party package source name
    /// that stamps every package the registry serves.
    /// </summary>
    public const string FirstPartySource = "Plugins";

    /// <summary>
    /// What a fresh instance PROVISIONS: everything the first-party source serves, as a PATTERN.
    ///
    /// <para>🚨 <b>A pattern, never an enumerated list</b>, and this is the whole point. The
    /// catalog already understands <c>Source/*</c> (<c>PluginCatalog:InstallByDefault</c>, matched
    /// source-scoped and failing closed on an unqualified id), so a wildcard covers what the source
    /// serves TODAY and what it publishes tomorrow. A hand-typed list of package names is stale the
    /// moment the next package ships, and — the failure that makes this worth stating — its symptom
    /// lands nowhere near its cause: the new package simply is not there, no error, no log line,
    /// and whoever published it sees a working store with their package missing from it.</para>
    /// </summary>
    public static readonly ImmutableList<string> ProvisionPackages =
        [$"{FirstPartySource}/*"];

    /// <summary>
    /// The modules a portal needs LOADED to render and serve — the registry-served set every
    /// first-party deployment declares under <c>Modules:Required</c>, so an absence is loud rather
    /// than a silently blank page.
    ///
    /// <para>These are <c>Required</c>, not <c>Assemblies</c>: they arrive from the registry, and a
    /// baseline entry would SHADOW the landed store module. The distinction is not cosmetic — it
    /// decides whether the deployment binds an app-closure copy the image may not even ship.</para>
    /// </summary>
    public static readonly ImmutableList<string> RequiredModules =
    [
        "MeshWeaver.Blazor.Radzen.dll",      // charts and the rich control pack
        "MeshWeaver.Blazor.Analysis.dll",    // analysis views
        "MeshWeaver.Blazor.EntityViews.dll", // entity edit forms
        "MeshWeaver.AI.dll",                 // the agent runtime + its catalogs
    ];

    /// <summary>
    /// Modules that ship IN THE IMAGE and are turned on by listing them — the
    /// <c>Modules:Assemblies</c> lane.
    ///
    /// <para>gRPC is default-on in every deployment: it is not only the foreign-participant
    /// transport, it is the React GUI's browser data plane. Delisting it silently breaks that
    /// frontend's live connection, which is why a fresh instance starts with it on.</para>
    /// </summary>
    public static readonly ImmutableList<string> BootModules =
    [
        "MeshWeaver.Hosting.Grpc.dll",
    ];

    /// <summary>
    /// What lands FOR EVERY USER rather than once for the instance. Empty by default and
    /// deliberately so: a package's own <c>preInstalled</c> declaration is the platform baseline,
    /// and everything beyond it costs every user storage they did not ask for. An operator adds to
    /// this knowingly.
    /// </summary>
    public static readonly ImmutableList<string> UserPreInstallPackages = [];

    /// <summary>The manifest a wizard opens with — every question pre-answered the way a working
    /// deployment answers it, and every answer still editable.</summary>
    public static InstanceManifest Manifest() => new()
    {
        State = InstanceSetupState.AwaitingStorage,
        Storage = new InstanceStorageSelection { Type = StorageType },
        BootModules = BootModules,
        ProvisionPackages = ProvisionPackages,
        UserPreInstallPackages = UserPreInstallPackages,
    };
}
