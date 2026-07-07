using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What an installable package delivers into the mesh.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackageKind>))]
public enum PackageKind
{
    /// <summary>Authored mesh content (agents, skills, model providers, docs, decks,
    /// course material) — installed by importing the folder's nodes into a partition.</summary>
    Content,

    /// <summary>Compiled capability (a NodeType renderer / layout areas) shipped as source
    /// compiled on the mesh. Reserved for the runtime-code-load stage — not installed yet.</summary>
    Code,
}

/// <summary>
/// The manifest describing one installable package. In the plugins repo each installable folder
/// carries a <c>package.json</c> that deserializes to this record; it is also the content of the
/// <c>Package</c> node written to the installed-packages registry so the catalog can show
/// installed / update-available status. Kept deliberately small.
/// </summary>
public record PackageManifest
{
    /// <summary>Stable package id (e.g. <c>"data-analyst-agent"</c>). Also the installed-record id.</summary>
    public string Id { get; init; } = "";

    /// <summary>Human-readable display name.</summary>
    public string? Name { get; init; }

    /// <summary>One-line description shown in the catalog.</summary>
    public string? Description { get; init; }

    /// <summary>What the package delivers (content today; code later).</summary>
    public PackageKind Kind { get; init; } = PackageKind.Content;

    /// <summary>The mesh partition the package installs into (e.g. <c>"Agent"</c>, <c>"Skill"</c>).</summary>
    public string? TargetPartition { get; init; }

    /// <summary>Package version string (compared to the installed record for update-available status).</summary>
    public string? Version { get; init; }

    /// <summary>The package's folder within the source repo. Set by the source while listing;
    /// not authored in <c>package.json</c>.</summary>
    public string? SourceFolder { get; init; }

    /// <summary>
    /// For <see cref="PackageKind.Code"/> packages only: the NodeType configuration lambda source
    /// (e.g. <c>"config =&gt; config.WithContentType&lt;Widget&gt;().AddLayout(...)"</c>). The installer
    /// synthesizes a <c>NodeType</c> node with this configuration and imports the package's
    /// <c>Source/*.cs</c> files as its Code nodes; the mesh then compiles it live (Roslyn) — no
    /// rebuild, no NuGet.
    /// </summary>
    public string? NodeTypeConfiguration { get; init; }

    /// <summary>Ids of other packages this one depends on. Advisory for now.</summary>
    public ImmutableList<string> Requires { get; init; } = [];

    // ── install-record metadata (null on catalog entries; set when written to the registry) ──

    /// <summary>The git ref (commit/branch) this package was installed from. Null until installed.</summary>
    public string? InstalledFromRef { get; init; }

    /// <summary>When the package was installed (UTC). Null until installed.</summary>
    public DateTimeOffset? InstalledAtUtc { get; init; }

    /// <summary>Number of content nodes upserted on the last install. Null until installed.</summary>
    public int? InstalledNodeCount { get; init; }
}

/// <summary>A single file of a package folder read from the source at a git ref.</summary>
/// <param name="RelativePath">Repo-relative path, e.g. <c>"data-analyst-agent/DataAnalyst.md"</c>.</param>
/// <param name="Content">UTF-8 file text.</param>
public sealed record PackageFile(string RelativePath, string Content);

/// <summary>
/// A source of installable packages — a git repo at a chosen ref. Lists the packages (folders with
/// a manifest) and fetches a package folder's files, so <see cref="PackageInstaller"/> can import
/// them. The one MVP implementation reads a LOCAL git repo via the <c>git</c> CLI (no NuGet); a
/// GitHub-fetch implementation slots in behind the same interface later.
/// </summary>
public interface IPackageSource
{
    /// <summary>Lists installable packages at <paramref name="gitRef"/> (each top-level folder that
    /// carries a <c>package.json</c>).</summary>
    IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef);

    /// <summary>Fetches every file of <paramref name="package"/>'s folder at <paramref name="gitRef"/>
    /// (the manifest itself is included; the installer skips it).</summary>
    IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef);
}
