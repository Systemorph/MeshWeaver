using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Articles;

public partial class ArticleView
{
    private readonly ModelParameter<Article> data = null!;
    private ActivityLog Log { get; set; } = null!;
    [Inject] private IToastService ToastService { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    protected override void BindData()
    {
        DataBind(
            ViewModel.Article,
            x => x.data,
            (jsonObject, _) => Convert(jsonObject as Article)
        );
        DataBind(
            ViewModel.IsPresentationMode,
            x => x.IsPresentationMode
        );
    }


    private ModelParameter<Article> ArticleModel { get; set; } = null!;
    private ModelParameter<Article>? Convert(Article? article)
    {
        if (article == null)
            return null;
        var ret = ArticleModel = new ModelParameter<Article>(article, (_, _) => throw new NotImplementedException());
        ret.ElementChanged += OnModelChanged!;
        return ret;
    }

    private void Reset()
    {
        data.Reset();
        InvokeAsync(StateHasChanged);
    }

    private void ShowSuccess()
    {
        var message = "Saved successfully";
        ToastService.ShowToast(ToastIntent.Success, message);
    }

    private void ShowError()
    {
        var message = "Saving failed";
        ToastService.ShowToast(ToastIntent.Error, message);
    }

    public override ValueTask DisposeAsync()
    {
        if (data != null)
            data.ElementChanged -= OnModelChanged;
        return base.DisposeAsync();
    }

    private void OnModelChanged(object? sender, Article e)
    {
        InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Unique kernel ID for this article view instance.
    /// Uses a stable ID based on the stream owner to ensure consistent routing.
    /// </summary>
    private string? _kernelId;
    private Address? _kernelAddress;
    private Address KernelAddress => _kernelAddress ??= AddressExtensions.CreateKernelAddress(KernelId);
    private string KernelId => _kernelId ??= $"article-{(Stream?.Owner?.ToString() ?? Guid.NewGuid().ToString("N"))[..Math.Min(Stream?.Owner?.ToString().Length ?? 32, 32)].Replace('/', '-')}";

    private bool _codeSubmitted;
    private bool _kernelNodeCreated;

    private MarkdownControl MarkdownControl
    {
        get => new(ArticleModel?.Element.Content ?? "") { Html = ConvertHtml(ArticleModel?.Element.PrerenderedHtml) };
    }


    private string? ConvertHtml(object? arg)
    {
        return arg?.ToString()?.Replace(ExecutableCodeBlockRenderer.KernelAddressPlaceholder, KernelAddress.ToString());
    }


    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (firstRender && !_codeSubmitted)
        {
            if (ArticleModel?.Element.CodeSubmissions is not null && ArticleModel?.Element.CodeSubmissions.Any() == true)
            {
                _codeSubmitted = true;

                // Create the kernel node first - required for proper routing
                // Without this, all kernel/* messages go to a single shared hub at "kernel"
                if (!_kernelNodeCreated)
                {
                    _kernelNodeCreated = true;
                    var kernelNode = new MeshNode(KernelId, AddressExtensions.KernelType)
                    {
                        Name = $"Kernel-{KernelId}",
                        NodeType = AddressExtensions.KernelType
                    };

                    try
                    {
                        var meshAddress = Hub.Configuration.ParentHub?.Address ?? Hub.Address;
                        var response = await Hub.AwaitResponse(
                            new CreateNodeRequest(kernelNode),
                            o => o.WithTarget(meshAddress));

                        // If node already exists, that's fine - it means another instance already created it
                        if (!response.Message.Success && !response.Message.Error?.Contains("already exists") == true)
                        {
                            Console.WriteLine($"Warning: Failed to create kernel node: {response.Message.Error}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Error creating kernel node: {ex.Message}");
                    }
                }

                // Now submit the code to the kernel
                foreach (var s in ArticleModel.Element.CodeSubmissions)
                    Hub.Post(s, o => o.WithTarget(KernelAddress));
            }
        }
    }


    private ArticleDisplayMode DisplayMode { get; set; }
    private Task ChangeDisplayMode(ArticleDisplayMode mode)
    {
        DisplayMode = mode;
        return InvokeAsync(StateHasChanged);
    }

    private bool IsPresentationMode { get; set; }

    private void TogglePresentationMode()
    {
        // Toggle presentation mode by navigating with query parameter
        var uri = new Uri(NavigationManager.Uri);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        if (IsPresentationMode)
        {
            query.Remove("presentation");
        }
        else
        {
            query["presentation"] = "true";
        }

        var newQuery = query.ToString();
        var newUrl = uri.GetLeftPart(UriPartial.Path) + (string.IsNullOrEmpty(newQuery) ? "" : "?" + newQuery);
        NavigationManager.NavigateTo(newUrl);

        IsPresentationMode = !IsPresentationMode;
        StateHasChanged();
    }
}
