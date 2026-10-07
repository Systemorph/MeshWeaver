namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The whole answer of one module adopt (<see cref="PluginBundleClient.AdoptModuleOutcome"/>): what
/// the registry served, what the decision said, what landed and — when nothing landed although
/// something should have — why. Init-only members, never a positional constructor: a public
/// record's constructor is a binary contract across the fleet.
/// </summary>
public sealed record ModuleAdoptOutcome
{
    /// <summary>The registry the adopt asked.</summary>
    public string? Registry { get; init; }

    /// <summary>The update decision, or null when the adopt failed before deciding.</summary>
    public ModuleUpdateVerdict? Verdict { get; init; }

    /// <summary>The version the registry's bundle index serves for the package, or null when it serves none.</summary>
    public string? ServedVersion { get; init; }

    /// <summary>The served bundle's declared platform floor (<c>minMeshVersion</c>), or null.</summary>
    public string? ServedFloor { get; init; }

    /// <summary>The version the activation record named before this adopt.</summary>
    public string? LandedBefore { get; init; }

    /// <summary>How many module files landed — zero when nothing did.</summary>
    public int FilesLanded { get; init; }

    /// <summary>The version that landed, when files landed.</summary>
    public string? LandedVersion { get; init; }

    /// <summary>Why nothing landed although the decision said land (a fetch miss, a refused
    /// bundle, a link-probe refusal) — or why the adopt could not decide at all. Null otherwise.</summary>
    public string? Failure { get; init; }

    /// <summary>True when <see cref="Failure"/> came from a CRASH — an exception, an unreachable
    /// registry, a pass that could not run — rather than a decided answer about the bundle. A module
    /// reload records such a failure as <c>Faulted</c> and retries it; a decided answer stays
    /// <c>Failed</c> (<c>Doc/Architecture/ModuleReload</c>).</summary>
    public bool Transient { get; init; }

    /// <summary>The registry serves no bundle for the package — the next configured registry may.</summary>
    public bool NotServed => Verdict?.Action == ModuleUpdateAction.SkipNoBundle;
}
