namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// The output format for a document export.
/// </summary>
public enum ExportFormat
{
    /// <summary>PDF via QuestPDF.</summary>
    Pdf,

    /// <summary>Microsoft Word .docx via DocumentFormat.OpenXml.</summary>
    Docx
}
