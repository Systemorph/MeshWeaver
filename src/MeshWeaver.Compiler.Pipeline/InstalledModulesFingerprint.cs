using Microsoft.Extensions.DependencyInjection;
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
/// <para><b>DECISIVE since #1664 Slice A:</b> the hash joins the usable-build decision —
/// <c>NodeTypeCompilationHelpers.HasUsableBuild</c> (and its rebuild-kickoff twin
/// <c>HasStaleFrameworkBuild</c>) invalidates a build stamped with a DIFFERENT non-null hash than
/// the live set, so a module-only update (framework MVID unchanged — the store-install lane,
/// where modules land in <c>modules/</c> without an image rebuild) forces the rebuild the
/// framework rule cannot see. Definitions stamped before the feature carry null, which compares
/// as MATCH: a null-hash build was compiled when modules were in the app closure, and the
/// framework rule already governs it. Call sites without a mesh in scope pass null and keep the
/// framework-only behavior.</para>
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
    public string Hash => Compute(modules);

    /// <summary>
    /// The fingerprint over the modules the mesh's container answers AT EACH READ — the registration
    /// <c>AddGraph</c> uses. A module in its own load context answers with its CURRENT generation
    /// (policy <c>module-live-update-default</c>), so after a live swap the hash changes and every
    /// build stamped with the old one reads as stale, which is what rebuilds a NodeType against the
    /// new generation on its next activation. The enumerable passed to the primary constructor is
    /// re-enumerated per read as well, so a caller handing a fixed list keeps a fixed hash.
    /// </summary>
    public static InstalledModulesFingerprint Live(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new InstalledModulesFingerprint(new LiveModuleSet(services));
    }

    private sealed class LiveModuleSet(IServiceProvider services) : IEnumerable<InstalledModuleAssembly>
    {
        public IEnumerator<InstalledModuleAssembly> GetEnumerator() =>
            services.GetServices<InstalledModuleAssembly>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

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
