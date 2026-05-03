using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Handlers;

/// <summary>
/// Handles <see cref="ExportDocumentRequest"/> by relaying it onto the
/// script-templated export pipeline. The handler builds an
/// <see cref="ExecuteScriptRequest"/> with caller-supplied
/// <see cref="ExecuteScriptRequest.Inputs"/>, dispatches it at the matching
/// Code template (<c>Templates/Export/Pdf</c> or <c>Templates/Export/Docx</c>),
/// subscribes to the resulting activity stream, and posts the rendered bytes
/// back as an <see cref="ExportDocumentResponse"/> when the activity completes.
///
/// <para>Pre-existing callers (Blazor view, Orleans test, …) keep firing
/// <see cref="ExportDocumentRequest"/> and reading bytes off the response —
/// the migration to script-driven execution is internal. See
/// <c>Doc/Architecture/ActivityControlPlane.md</c> → "Operations as scripts".</para>
/// </summary>
public static class ExportDocumentHandler
{
    /// <summary>
    /// Registers the handler on a hub configuration. Registered inside <c>AddMarkdownExport()</c>.
    /// </summary>
    public static MessageHubConfiguration AddExportDocumentHandler(this MessageHubConfiguration config)
    {
        config.TypeRegistry.AddMarkdownExportTypes();
        return config.WithHandler<ExportDocumentRequest>(Handle);
    }

    private static IMessageDelivery Handle(
        IMessageHub hub, IMessageDelivery<ExportDocumentRequest> delivery)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ExportDocumentHandler).FullName!);
        var request = delivery.Message;
        var jsonOptions = hub.JsonSerializerOptions;

        var inputs = BuildInputs(request, jsonOptions);
        var templatePath = ResolveTemplatePath(request.Options.Format);

        return ScriptDispatch.RelayToScript<ExportDocumentRequest, ExportDocumentResponse>(
            hub,
            delivery,
            templatePath,
            inputs,
            mapSuccess: returnValue => DeserializeResponse(returnValue, request, jsonOptions),
            mapFailure: reason => new ExportDocumentResponse(
                request.Options.Format, "", "", [], Error: reason),
            logger: logger);
    }

    private static ImmutableDictionary<string, JsonElement> BuildInputs(
        ExportDocumentRequest request, JsonSerializerOptions jsonOptions)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>();
        builder["sourcePath"] = JsonSerializer.SerializeToElement(request.SourcePath, jsonOptions);
        builder["options"] = JsonSerializer.SerializeToElement(request.Options, jsonOptions);
        if (!string.IsNullOrEmpty(request.Options.Title))
            builder["title"] = JsonSerializer.SerializeToElement(request.Options.Title, jsonOptions);
        if (!string.IsNullOrEmpty(request.Options.BrandNodePath))
            builder["brandNodePath"] = JsonSerializer.SerializeToElement(request.Options.BrandNodePath, jsonOptions);
        return builder.ToImmutable();
    }

    private static string ResolveTemplatePath(ExportFormat format) => format switch
    {
        ExportFormat.Pdf => $"{MarkdownExportTemplates.TemplatesNamespace}/{MarkdownExportTemplates.ExportPdfId}",
        ExportFormat.Docx => $"{MarkdownExportTemplates.TemplatesNamespace}/{MarkdownExportTemplates.ExportDocxId}",
        _ => throw new NotSupportedException($"Unsupported format {format}")
    };

    private static ExportDocumentResponse DeserializeResponse(
        JsonElement? returnValue,
        ExportDocumentRequest request,
        JsonSerializerOptions jsonOptions)
    {
        if (returnValue is { } el && el.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var deserialized = el.Deserialize<ExportDocumentResponse>(jsonOptions);
                if (deserialized is not null && deserialized.Content.Length > 0)
                    return deserialized;
            }
            catch
            {
                // Fall through to a structured "no content" failure response.
            }
        }
        return new ExportDocumentResponse(
            request.Options.Format, "", "", [],
            Error: "Export script returned no content");
    }
}
