using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The mesh-scoped fingerprint of the deployment's installed MODULES — a stable hash over the
/// sorted MVIDs of every <see cref="InstalledModuleAssembly"/> (the <c>Modules:Assemblies</c>
/// set, design #1644). Stamped as <c>NodeTypeDefinition.CompiledModulesHash</c> by every
/// successful NodeType compile, alongside <c>CompiledFrameworkVersion</c>.
///
/// <para><b>Step-1 scope (deliberate):</b> the hash is RECORDED but does not yet join the
/// usable-build decision. While modules ride the app image, a module change rebuilds the image,
/// which changes Graph's MVID, which invalidates every build through the existing framework-match
/// rule — the modules hash is redundant there. It becomes load-bearing exactly when modules ship
/// SEPARATELY from the image (the <c>modules/</c> folder + bundle lane, #1644 step 2): then a
/// module-only update must invalidate baked builds that could reference it, and this recorded
/// hash is what the usable-build check compares. Definitions stamped before this feature carry
/// null, which step 2 must treat as MATCH: a null-hash build was compiled when modules were in
/// the app closure, and the framework rule already governs it.</para>
///
/// <para>Registered as a mesh-scoped singleton (lifetime = the mesh, never static — test meshes
/// with different module sets must not bleed).</para>
/// </summary>
public sealed class InstalledModulesFingerprint(IEnumerable<InstalledModuleAssembly> modules)
{
    /// <summary>
    /// The hash: lowercase hex SHA-256 over the ordinal-sorted module MVIDs, or the empty string
    /// for a mesh with no installed modules (stable, distinguishable from the null of
    /// pre-feature definitions).
    /// </summary>
    public string Hash { get; } = Compute(modules);

    private static string Compute(IEnumerable<InstalledModuleAssembly> modules)
    {
        var mvids = modules
            .Select(m => m.Mvid.ToString("N"))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (mvids.Length == 0)
            return string.Empty;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", mvids)));
        return Convert.ToHexStringLower(bytes);
    }
}
