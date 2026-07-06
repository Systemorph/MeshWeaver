using System.Text.Json;
using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Blazor view for <see cref="DocumentSourceControl"/> — renders an original PDF/DOCX inline and
/// highlights a passage in it. Mirrors the sanctioned JS-interop pattern (collocated
/// <c>.razor.js</c> module loaded on first render, the only place async is allowed in a view): the
/// module drives PDF.js / mammoth in the browser, locates the verbatim passage in the rendered text,
/// and marks it. Unsupported types or a failed library load degrade to a download link plus the
/// passage text — never a blank pane.
/// </summary>
public partial class DocumentSourceView
{
    private ElementReference containerRef;
    private IJSObjectReference? jsModule;

    private object? FileUrlRaw { get; set; }
    private object? MimeRaw { get; set; }
    private object? HighlightRaw { get; set; }
    private object? FileNameRaw { get; set; }
    private object? PageRaw { get; set; }
    private object? BoxRaw { get; set; }

    private string? FileUrl { get; set; }
    private string? Mime { get; set; }
    private string? Highlight { get; set; }
    private string? FileName { get; set; }
    private int? Page { get; set; }
    private string? Box { get; set; }

    // Guards against re-invoking the (expensive) render when nothing that affects it changed
    // — the base re-runs BindData on every parameter set.
    private string? _renderedUrl;
    private string? _renderedHighlight;
    private string? _renderedMark;

    /// <inheritdoc />
    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.FileUrl, x => x.FileUrlRaw);
        DataBind(ViewModel.Mime, x => x.MimeRaw);
        DataBind(ViewModel.Highlight, x => x.HighlightRaw);
        DataBind(ViewModel.FileName, x => x.FileNameRaw);
        DataBind(ViewModel.Page, x => x.PageRaw);
        DataBind(ViewModel.Box, x => x.BoxRaw);

        FileUrl = Coerce(FileUrlRaw);
        Mime = Coerce(MimeRaw);
        Highlight = Coerce(HighlightRaw);
        FileName = Coerce(FileNameRaw);
        Box = Coerce(BoxRaw);
        Page = int.TryParse(Coerce(PageRaw), out var page) ? page : null;
    }

    /// <summary>
    /// Loads the interop module on first render, then (re)renders whenever the file or passage
    /// changes. JS interop in <c>OnAfterRenderAsync</c> is the one sanctioned async surface in a view.
    /// </summary>
    /// <param name="firstRender">True on the very first render of this component instance.</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                jsModule = await JSRuntime.InvokeAsync<IJSObjectReference>(
                    "import", "./_content/MeshWeaver.Blazor/Components/DocumentSourceView.razor.js");
            }
            catch (JSDisconnectedException) { return; }
        }

        if (jsModule is null || string.IsNullOrEmpty(FileUrl))
            return;

        var mark = $"{Page}|{Box}";
        if (FileUrl == _renderedUrl && Highlight == _renderedHighlight && mark == _renderedMark)
            return;
        _renderedUrl = FileUrl;
        _renderedHighlight = Highlight;
        _renderedMark = mark;

        try
        {
            await jsModule.InvokeVoidAsync("render", containerRef, FileUrl, Mime, Highlight, FileName, Page, Box);
        }
        catch (JSDisconnectedException) { /* circuit gone — best-effort */ }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "DocumentSourceView render failed for {Url}", FileUrl);
        }
    }

    private static string? Coerce(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        JsonElement je => je.ToString(),
        _ => value.ToString(),
    };

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (jsModule != null)
        {
            try
            {
                await jsModule.InvokeVoidAsync("dispose", containerRef);
                await jsModule.DisposeAsync();
            }
            catch (JSDisconnectedException) { /* circuit already gone — teardown is best-effort */ }
            catch { /* ignore */ }
            jsModule = null;
        }
        await base.DisposeAsync();
    }
}
