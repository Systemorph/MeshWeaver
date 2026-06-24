using MeshWeaver.Kernel;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Components;

public partial class MarkdownView
{
    private object? MarkdownRaw { get; set; }
    private object? HtmlRaw { get; set; }
    private object? CodeSubmissionsRaw { get; set; }
    private object? ShowReferencesRaw { get; set; }
    private object? NodePathRaw { get; set; }

    private string? Html { get; set; }
    private IReadOnlyList<SubmitCodeRequest>? CodeSubmissions { get; set; }
    public bool ShowReferencesSection { get; set; } = true;

    // Per-view kernel session id. Used as the Activity MeshNode id below.
    private readonly string _kernelId = Guid.NewGuid().AsString();
    private Address? _kernelAddress;

    // The "kernel address" is now the per-view Activity path
    // (`{owner}/_Activity/markdown-{kernelId}`). The Activity hub hosts the
    // kernel handlers via `ActivityNodeType.HubConfiguration` +
    // `AddKernelSubHubHandlers`. Replaces the legacy `kernel/*` standalone
    // hub addressing — replies route through the standard MeshNode chain.
    private Address KernelAddress => _kernelAddress ??= ResolveActivityAddress();

    private bool _codeSubmitted;

    // Flipped (via CreateActivityAndSubmit's onReady) once the per-view Activity node is created and
    // routable. Gates the LIVE kernel-area embed in RenderHtml: until it's true the kernel result
    // areas render as a non-subscribing placeholder, so the GUI never subscribes to a not-yet-created
    // {owner}/_Activity/markdown-{id} (the subscribe-before-create NotFound storm).
    private bool _kernelReady;

    // Memoised parse — the parent re-renders this view on EVERY streaming tick with a fresh
    // MarkdownControl whose text is usually identical (a completed chat bubble while a LATER
    // cell streams). BlazorView.OnParametersSet re-runs BindData each time, so without this
    // every unchanged bubble re-runs Markdig per tick — O(bubbles × ticks) server CPU that pegs
    // the circuit and stutters Safari. Reuse the previous parse when (markdown, owner, nodePath)
    // are unchanged; the streaming cell's text DOES change each tick, so it re-parses correctly.
    private string? _memoMarkdown;
    private string? _memoNodePath;
    private string? _memoOwner;
    private string? _memoHtml;
    private IReadOnlyList<SubmitCodeRequest>? _memoCodeSubmissions;

    private Address ResolveActivityAddress()
    {
        var ownerPath = Stream?.Owner?.Path;
        var activityNamespace = string.IsNullOrEmpty(ownerPath)
            ? "_Activity"
            : $"{ownerPath}/_Activity";
        return new Address($"{activityNamespace}/markdown-{_kernelId}");
    }

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Markdown, x => x.MarkdownRaw);
        DataBind(ViewModel.Html, x => x.HtmlRaw);
        DataBind(ViewModel.CodeSubmissions, x => x.CodeSubmissionsRaw);
        DataBind(ViewModel.ShowReferences, x => x.ShowReferencesRaw);
        DataBind(ViewModel.NodePath, x => x.NodePathRaw);

        var markdown = MarkdownViewLogic.CoerceString(MarkdownRaw);
        Html = MarkdownViewLogic.CoerceString(HtmlRaw);
        CodeSubmissions = MarkdownViewLogic.CoerceCodeSubmissions(CodeSubmissionsRaw, Hub.JsonSerializerOptions);
        ShowReferencesSection = MarkdownViewLogic.CoerceBool(ShowReferencesRaw, defaultValue: true);

        // Explicit NodePath (set by the producing control) wins over the bound stream's owner.
        // Relative @@-embeds resolve against this path; child controls whose stream owner is
        // not the authoring node (e.g. a Space's body inside the Overview) rely on it.
        var nodePath = MarkdownViewLogic.CoerceString(NodePathRaw) ?? Stream?.Owner?.ToString();

        if (Html is null && !string.IsNullOrEmpty(markdown))
        {
            var ownerKey = Stream?.Owner?.ToString();
            if (markdown == _memoMarkdown && nodePath == _memoNodePath && ownerKey == _memoOwner
                && _memoHtml is not null)
            {
                // Identical input → identical parse. Skip Markdig (see _memoMarkdown).
                Html = _memoHtml;
                CodeSubmissions ??= _memoCodeSubmissions;
            }
            else
            {
                var result = MarkdownViewLogic.Render(markdown, Stream?.Owner, nodePath);
                Html = result.Html;
                CodeSubmissions ??= result.CodeSubmissions;
                _memoMarkdown = markdown;
                _memoNodePath = nodePath;
                _memoOwner = ownerKey;
                _memoHtml = result.Html;
                _memoCodeSubmissions = result.CodeSubmissions;
            }
        }
        else if (CodeSubmissions is null
                 && Html is not null
                 && Html.Contains(ExecutableCodeBlockRenderer.KernelAddressPlaceholder)
                 && !string.IsNullOrEmpty(markdown))
        {
            CodeSubmissions = MarkdownViewLogic.ExtractCodeSubmissions(
                markdown, Stream?.Owner, nodePath);
        }

        // NB: the kernel result-area placeholder is left UNRESOLVED here. The decision of whether to
        // embed a live (subscribing) area, a non-subscribing "starting" placeholder, or a "no owner"
        // notice is made at RENDER time in RenderHtml (gated on _kernelReady), so flipping _kernelReady
        // after the activity is routable simply re-renders with the live area — without re-running
        // Markdig. See RenderHtml + OnAfterRenderAsync.
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (firstRender && !_codeSubmitted && CodeSubmissions is { Count: > 0 })
        {
            _codeSubmitted = true;
            var ownerPath = Stream?.Owner?.Path;
            if (string.IsNullOrEmpty(ownerPath))
            {
                // No owning node → no per-node hub to host the kernel. Do NOT create a bare
                // `_Activity/markdown-*` activity (it would NotFound-storm the router). The result
                // areas were neutralised into a notice in BindData; just log and skip submission.
                Hub.ServiceProvider.GetService<ILoggerFactory>()?
                    .CreateLogger("MarkdownExecution")
                    .LogWarning(
                        "Markdown view {Kernel}: skipping interactive code execution — the view has no owning node (Stream.Owner is empty).",
                        _kernelId);
                return;
            }
            var meshService = Hub.ServiceProvider.GetRequiredService<IMeshService>();
            MarkdownViewLogic.CreateActivityAndSubmit(
                Hub, meshService, KernelAddress, ownerPath, _kernelId, CodeSubmissions,
                onReady: OnKernelReady);
        }
    }

    // Invoked (off the Blazor renderer) once the per-view Activity is created + routable. Flip the
    // gate and re-render: RenderHtml now embeds the LIVE kernel area, so the LayoutAreaView(s)
    // subscribe to an address that already exists — no NotFound storm.
    private void OnKernelReady()
    {
        if (IsViewDisposed || _kernelReady) return;
        InvokeAsync(() =>
        {
            if (IsViewDisposed) return;
            _kernelReady = true;
            StateHasChanged();
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }

    private void RenderHtml(RenderTreeBuilder builder)
    {
        if (Html is null)
            return;

        // Resolve the kernel result-area placeholder at render time: a live (subscribing) area only
        // once the activity is routable (_kernelReady), a non-subscribing "starting" placeholder
        // until then, or a "no owner" notice when there's no hub to host the kernel. This is the gate
        // that prevents the subscribe-before-create NotFound storm.
        var html = CodeSubmissions is { Count: > 0 }
            ? MarkdownViewLogic.RenderKernelResultAreas(Html, Stream?.Owner?.Path, _kernelReady, KernelAddress)
            : Html;

        var renderer = new MarkdownHtmlRenderer(Mode, Stream);
        renderer.ShowReferencesSection = ShowReferencesSection;
        renderer.RenderHtml(builder, html);
    }
}
