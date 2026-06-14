using MeshWeaver.Kernel;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

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
            var result = MarkdownViewLogic.Render(markdown, Stream?.Owner, nodePath);
            Html = result.Html;
            CodeSubmissions ??= result.CodeSubmissions;
        }
        else if (CodeSubmissions is null
                 && Html is not null
                 && Html.Contains(ExecutableCodeBlockRenderer.KernelAddressPlaceholder)
                 && !string.IsNullOrEmpty(markdown))
        {
            CodeSubmissions = MarkdownViewLogic.ExtractCodeSubmissions(
                markdown, Stream?.Owner, nodePath);
        }

        if (Html is not null && CodeSubmissions is { Count: > 0 })
            Html = MarkdownViewLogic.ReplaceKernelPlaceholder(Html, KernelAddress);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (firstRender && !_codeSubmitted && CodeSubmissions is { Count: > 0 })
        {
            _codeSubmitted = true;
            var meshService = Hub.ServiceProvider.GetRequiredService<IMeshService>();
            var ownerPath = Stream?.Owner?.Path;
            MarkdownViewLogic.CreateActivityAndSubmit(
                Hub, meshService, KernelAddress, ownerPath, _kernelId, CodeSubmissions);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }

    private void RenderHtml(RenderTreeBuilder builder)
    {
        if (Html is null)
            return;

        var renderer = new MarkdownHtmlRenderer(Mode, Stream);
        renderer.ShowReferencesSection = ShowReferencesSection;
        renderer.RenderHtml(builder, Html);
    }
}
