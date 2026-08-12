# MeshWeaver.Markdown.Export.Contract

The wire contract for document export — the types a **caller** needs to request an export and read
the result back. It carries no renderer: the document model, the DOCX writer, the print composers
and the browser-driving PDF path all stay in
[MeshWeaver.Markdown.Export](../MeshWeaver.Markdown.Export/README.md), along with the packages they
need.

That split exists so a UI can drive an export without depending on the engine that performs it.
`MeshWeaver.Blazor` references this assembly only, which keeps the ~6.5k-line renderer out of the
Blazor layer's project graph.

> Scope note: this removes the *engine assembly*, not the third-party packages. Blazor still
> resolves `DocumentFormat.OpenXml` (through `MeshWeaver.ContentCollections` →
> ClosedXML / DocSharp.Docx), `HtmlAgilityPack` (used directly by `MarkdownHtmlRenderer`) and
> `Markdig` (with `MeshWeaver.Markdown`). Those arrive by their own routes and are unaffected.

Contents:

| Type | Role |
|---|---|
| `ExportDocumentRequest` / `ExportDocumentResponse` | The hub request and its start-acknowledgement (the response carries the Activity path, not the bytes). |
| `DocumentExportOptions` | What the export dialog collects: format, fidelity, branding, cover, TOC, page-break rules. |
| `ExportFormat`, `ExportFidelity` | The output format, and how faithfully it reproduces the browser's rendering. |
| `RenderedDocument` | The produced file — name, mime type, bytes. Travels through `ActivityLog.ReturnValue`. |
| `CorporateIdentity` | Content of a `CorporateIdentity` mesh node: the branding an export applies. |
| `AddMarkdownExportTypes()` | Registers all of the above on a hub's `ITypeRegistry`. |

`ExportDocumentControl` is deliberately **not** here — it lives in `MeshWeaver.Layout` with every
other `UiControl`, because Layout is the control vocabulary that the server and all four renderers
(Blazor, React, React Native, MAUI) agree on.

## Consuming an export

The engine does not return bytes from the handler; it starts an Activity and acknowledges. Callers
subscribe to that Activity and read `ActivityLog.ReturnValue` on the terminal snapshot:

```csharp
hub.Observe(new ExportDocumentRequest(sourcePath, options), o => o.WithTarget(new Address(sourcePath)))
   .Take(1)
   .SelectMany(dispatch => cache.GetStream(dispatch.Message.ActivityPath, hub.JsonSerializerOptions)
       .Select(n => n.ContentAs<ActivityLog>(hub.JsonSerializerOptions))
       .Where(log => log is not null && log.Status != ActivityStatus.Running)
       .Take(1))
   .Subscribe(terminal => { /* terminal.ReturnValue → RenderedDocument */ });
```

Why a start-ack rather than the bytes: waiting for the render inside the handler would block the
hub's action block while the script does cross-hub work. See
[AsynchronousCalls.md](../MeshWeaver.Documentation/Data/Architecture/AsynchronousCalls.md).

🚨 **Scripts.** The built-in `.csx` export templates compile at *runtime* against this assembly, so
it is registered as a `KernelScriptAssembly` alongside the engine in `AddMarkdownExport()`. A new
type here is visible to those templates; removing one breaks them with no compiler signal.
