using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Mesh;

[assembly: MeshWeaver.Markdown.Export.MarkdownExportProvider]

namespace MeshWeaver.Markdown.Export;

/// <summary>
/// Boot-pack entry point: loading this assembly via <c>Modules:Assemblies</c> installs the full
/// markdown-export pipeline through the SAME <see cref="MarkdownExportExtensions.AddMarkdownExport"/>
/// call a compiled-in portal makes — one code path, so the boot-pack and compiled-in registrations
/// cannot drift. Carried as a <see cref="MeshNodeProviderAttribute.BuilderConfigurations"/> hook
/// because the registration spans the whole builder surface (mesh types, static template nodes,
/// the Templates/Export access grant, per-node-hub menus/areas/handler).
/// </summary>
public sealed class MarkdownExportProviderAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations =>
        [builder => builder.AddMarkdownExport()];
}
