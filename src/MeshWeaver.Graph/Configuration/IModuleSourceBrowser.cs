using System.Reactive.Linq;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>One compile-input file of a module, as the source browser lists it.</summary>
/// <param name="NodePath">The mesh path the file WOULD have as a node (<c>{Package}/{Type}/Source/{Name}</c>)
/// — the address the NodeType shell's Code area navigates by, so a browsed file and an imported
/// one share one URL shape.</param>
/// <param name="RelativePath">The repo-relative file path (<c>{Package}/{Type}/Source/{Name}.cs</c>).</param>
/// <param name="FileName">The display name (the file name).</param>
public sealed record ModuleSourceFile(string NodePath, string RelativePath, string FileName);

/// <summary>
/// Browses a module's SOURCE from its repository, through the registry — the read seam of
/// MeshWeaver#2193 §C. On an adopt-only mesh (<see cref="PrebuiltAssemblySeeder.ImportSourceNodesConfigKey"/>
/// off) the <c>Source/</c>/<c>Test/</c> files are no longer persisted as nodes; their text lives
/// in the package repo, and this seam serves it: the NodeType shell's Sources/Tests trees list
/// through <see cref="ListSources"/> and the Code area reads through <see cref="FetchSource"/>.
///
/// <para>🚨 The credential is the REGISTRY's, never the consumer's. The consumer asks the
/// registry it already installs from, with the instance key it already holds; the registry
/// resolves the package from its curated catalog and reads the file with its own GitHub App
/// credential — the same encapsulation that serves the packages themselves. A private repo is
/// browsable exactly as far as the registry grants the package, and a consumer never holds a
/// GitHub credential for it.</para>
///
/// <para>Registered by the plugin catalog (<c>RegistrySourceBrowser</c>). A mesh with no
/// registry access simply has no browser registered — the shell then says so
/// (<see cref="ModuleSourceBrowsing.NeedsRegistryMarkdown"/>) rather than rendering an empty tree
/// that reads like a module with no code.</para>
/// </summary>
public interface IModuleSourceBrowser
{
    /// <summary>The compile-input files of <paramref name="packageId"/>, from the registry's
    /// package manifest. Empty when the registry does not serve the package.</summary>
    IObservable<IReadOnlyList<ModuleSourceFile>> ListSources(string packageId);

    /// <summary>The text of the file that would be the node at <paramref name="nodePath"/>, or
    /// null when the registry does not serve it.</summary>
    IObservable<string?> FetchSource(string packageId, string nodePath);
}

/// <summary>Pure helpers shared by the shell and the browsers.</summary>
public static class ModuleSourceBrowsing
{
    /// <summary>What the shell renders when sources are not on the mesh and no registry can
    /// serve them.</summary>
    public const string NeedsRegistryMarkdown =
        "*Source browsing needs the registry.* This mesh runs modules from prebuilt assemblies "
        + "and keeps no source nodes; the source is read from the module's repository through "
        + "the registry this mesh installs from — and no registry is configured or reachable here.";

    /// <summary>What the shell renders for a file the registry does not serve.</summary>
    public static string NotServedMarkdown(string nodePath) =>
        $"*The registry does not serve a source file for `{nodePath}`.* Either the package no "
        + "longer ships it, or this mesh's registry grant does not cover the package.";

    /// <summary>The package a mesh path belongs to — its partition root. Pure.</summary>
    public static string PackageOf(string path)
    {
        var slash = path.IndexOf('/');
        return slash > 0 ? path[..slash] : path;
    }

    /// <summary>Whether this mesh browses source REMOTELY: it does not persist source nodes
    /// (<see cref="PrebuiltAssemblySeeder.ImportSourceNodesConfigKey"/> off). Pure over config.</summary>
    public static bool BrowsesRemotely(IServiceProvider? services) =>
        !PrebuiltAssemblySeeder.ImportSourceNodes(services);

    /// <summary>A stand-in node for a browsed file, so the shell's existing code tree — which
    /// walks nodes — renders the registry's listing unchanged: same path, same label, the Code
    /// node type, no content. Pure.</summary>
    public static MeshNode Synthesize(ModuleSourceFile file) =>
        MeshNode.FromPath(file.NodePath) with
        {
            Name = file.FileName,
            NodeType = "Code",
        };
}
