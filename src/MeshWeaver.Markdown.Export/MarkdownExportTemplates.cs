using MeshWeaver.Mesh;

namespace MeshWeaver.Markdown.Export;

/// <summary>
/// Built-in export script templates seeded as <see cref="MeshNode"/>s of type
/// <c>Code</c>. The <c>.csx</c> bodies travel inside this assembly as embedded
/// resources (<c>Templates/ExportPdf.csx</c>, <c>Templates/ExportDocx.csx</c>);
/// each one becomes an executable Code node at
/// <c>Templates/Export/{format}</c> that the kernel runs when an
/// <see cref="ExecuteScriptRequest"/> arrives carrying caller-supplied
/// <see cref="ExecuteScriptRequest.Inputs"/> (<c>sourcePath</c>, <c>options</c>,
/// <c>brandNodePath</c>, <c>title</c>).
///
/// <para>Stateless static helper — registered via <c>builder.AddMeshNodes(...)</c>
/// in <c>AddMarkdownExport</c> rather than as an <c>IStaticNodeProvider</c>
/// through DI, since it holds no state. See
/// <c>Doc/Architecture/AsynchronousCalls.md</c> → "Static handlers compose —
/// don't wrap them in a service for 'DI cleanliness'".</para>
///
/// <para>This is the canonical "operations as scripts" shape — see
/// <c>Doc/Architecture/ActivityControlPlane.md</c> → "Operations as scripts".</para>
/// </summary>
public static class MarkdownExportTemplates
{
    /// <summary>Namespace under which the export Code template nodes are seeded.</summary>
    public const string TemplatesNamespace = "Templates/Export";
    /// <summary>Node id of the PDF export Code template.</summary>
    public const string ExportPdfId = "Pdf";
    /// <summary>Node id of the DOCX export Code template.</summary>
    public const string ExportDocxId = "Docx";

    /// <summary>
    /// Returns the seeded Code MeshNodes for the PDF and DOCX export templates.
    /// Lazy-loaded once per process; the embedded <c>.csx</c> texts are read on
    /// first call and cached.
    /// </summary>
    public static IEnumerable<MeshNode> GetStaticNodes() => LazyNodes.Value;

    private static readonly Lazy<MeshNode[]> LazyNodes = new(LoadAllNodes);

    private static MeshNode[] LoadAllNodes()
    {
        var pdfCode = ReadEmbeddedResource("Templates.ExportPdf.csx");
        var docxCode = ReadEmbeddedResource("Templates.ExportDocx.csx");

        return
        [
            BuildCodeNode(
                ExportPdfId,
                "Export to PDF",
                "Renders a markdown node (and optional descendants) to PDF. Triggered by ExecuteScriptRequest with Inputs.sourcePath.",
                pdfCode),
            BuildCodeNode(
                ExportDocxId,
                "Export to DOCX",
                "Renders a markdown node (and optional descendants) to DOCX. Triggered by ExecuteScriptRequest with Inputs.sourcePath.",
                docxCode),
        ];
    }

    private static MeshNode BuildCodeNode(string id, string name, string description, string code) =>
        new(id, TemplatesNamespace)
        {
            NodeType = "Code",
            Name = name,
            Description = description,
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Code = code,
                Language = "csharp",
                IsExecutable = true,
                // Each run lands in the caller's home — exports are user-scoped,
                // not template-scoped. Same convention as the docs partition.
                ActivityParentPath = "{viewer}"
            }
        };

    private static string ReadEmbeddedResource(string relativeName)
    {
        var assembly = typeof(MarkdownExportTemplates).Assembly;
        var prefix = $"{assembly.GetName().Name}.";
        var fullName = prefix + relativeName;
        using var stream = assembly.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{fullName}' not found. Ensure it is included as <EmbeddedResource> in the .csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
