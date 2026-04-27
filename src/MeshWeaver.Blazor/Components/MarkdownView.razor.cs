using MeshWeaver.Kernel;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.Components.Rendering;

namespace MeshWeaver.Blazor.Components;

public partial class MarkdownView
{
    private object? MarkdownRaw { get; set; }
    private object? HtmlRaw { get; set; }
    private object? CodeSubmissionsRaw { get; set; }
    private object? ShowReferencesRaw { get; set; }

    private string? Html { get; set; }
    private IReadOnlyList<SubmitCodeRequest>? CodeSubmissions { get; set; }
    public bool ShowReferencesSection { get; set; } = true;

    private readonly string _kernelId = Guid.NewGuid().AsString();
    private Address? _kernelAddress;
    private Address KernelAddress => _kernelAddress ??= AddressExtensions.CreateKernelAddress(_kernelId);

    private bool _codeSubmitted;

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Markdown, x => x.MarkdownRaw);
        DataBind(ViewModel.Html, x => x.HtmlRaw);
        DataBind(ViewModel.CodeSubmissions, x => x.CodeSubmissionsRaw);
        DataBind(ViewModel.ShowReferences, x => x.ShowReferencesRaw);

        var markdown = MarkdownViewLogic.CoerceString(MarkdownRaw);
        Html = MarkdownViewLogic.CoerceString(HtmlRaw);
        CodeSubmissions = MarkdownViewLogic.CoerceCodeSubmissions(CodeSubmissionsRaw, Hub.JsonSerializerOptions);
        ShowReferencesSection = MarkdownViewLogic.CoerceBool(ShowReferencesRaw, defaultValue: true);

        if (Html is null && !string.IsNullOrEmpty(markdown))
        {
            var result = MarkdownViewLogic.Render(markdown, Stream?.Owner, Stream?.Owner?.ToString());
            Html = result.Html;
            CodeSubmissions ??= result.CodeSubmissions;
        }
        else if (CodeSubmissions is null
                 && Html is not null
                 && Html.Contains(ExecutableCodeBlockRenderer.KernelAddressPlaceholder)
                 && !string.IsNullOrEmpty(markdown))
        {
            CodeSubmissions = MarkdownViewLogic.ExtractCodeSubmissions(
                markdown, Stream?.Owner, Stream?.Owner?.ToString());
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
            MarkdownViewLogic.SubmitCode(Hub, KernelAddress, CodeSubmissions);
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
