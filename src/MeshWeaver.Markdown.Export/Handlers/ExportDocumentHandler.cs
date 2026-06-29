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
/// Handles <see cref="ExportDocumentRequest"/> by <b>starting</b> the
/// script-templated export pipeline and posting back a start-ack with the
/// activity path. The handler does NOT wait for the rendered bytes — that
/// would block the action block under load while the script does cross-hub
/// work (per <c>Doc/Architecture/AsynchronousCalls.md</c> → "🚨 NOTHING ASYNC
/// EVER"). Callers (Blazor view, MCP, tests) subscribe to
/// <c>workspace.GetMeshNodeStream(ActivityPath)</c> for progress and read the
/// rendered bytes from <c>ActivityLog.ReturnValue</c> on terminal status.
///
/// <para>Pipeline:
/// <c>ExportDocumentRequest → ScriptDispatch.StartScript → ExecuteScriptRequest
/// at Templates/Export/{Pdf,Docx} → kernel runs script → ActivityLog ticks live →
/// caller reads ActivityLog.ReturnValue on terminal</c>.</para>
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

        return ScriptDispatch.StartScript<ExportDocumentRequest, ExportDocumentResponse>(
            hub,
            delivery,
            templatePath,
            inputs,
            mapStarted: started => new ExportDocumentResponse(
                request.Options.Format, started.ActivityPath),
            mapFailure: reason => new ExportDocumentResponse(
                request.Options.Format, "", Error: reason),
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
}
