using MeshWeaver.Mesh;

[assembly: MeshWeaver.Import.ImportMeshModule]

namespace MeshWeaver.Import;

/// <summary>
/// The import stack as a module: <c>MeshWeaver.Import</c> plus its private
/// <c>MeshWeaver.DataSetReader.*</c> closure (CSV, the two Excel formats, the shared utils) —
/// tabular ingestion, the Excel/CSV readers, the mapping configuration and the
/// <c>ImportRequest</c> handler.
///
/// <para>🚨 <b>This module registers nothing.</b> That is deliberate and it is the whole point.
/// No host ever called <c>AddImport()</c>: <c>MessageHubConfiguration.AddImport(...)</c> is an
/// APPLICATION-level call a data source makes for itself, and both portals referenced this
/// assembly for exactly one reason — so that IN-MESH source could <c>using MeshWeaver.Import</c>.
/// NodeType sources compile against <c>TRUSTED_PLATFORM_ASSEMBLIES</c> composed with the
/// deployment's installed modules (<c>CompileReferences.ComposeWithModules</c>), and
/// <c>MeshBuilder.InstallAssemblies</c> registers an <c>InstalledModuleAssembly</c> for every
/// listed DLL — so listing this one preserves that compile surface exactly, with no compiled
/// reference from either host.</para>
///
/// <para>🚨 <b>Order matters in <c>Modules:Assemblies</c>.</b> The compile reference set is
/// composed from the installed modules, so this DLL must be listed BEFORE any module whose own
/// content compiles against it. It is listed first in both portals.</para>
///
/// <para>This is NOT a dependency win, and must not be advertised as one: <c>ClosedXML</c>,
/// <c>DocumentFormat.OpenXml</c> and <c>CsvHelper</c> stay in the image regardless, pulled in by
/// <c>MeshWeaver.Blazor</c> and <c>MeshWeaver.ContentCollections</c>. What the flip buys is that
/// the import stack itself — and the DataSetReader family, which nothing else references — is a
/// deployment's choice rather than every portal's fixed cost.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ImportMeshModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.Import")
        {
            Name = "Import (Excel / CSV)",
            NodeType = "ModuleDefinition",
        },
    ];
}
