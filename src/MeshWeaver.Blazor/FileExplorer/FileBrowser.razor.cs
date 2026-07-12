using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.FileExplorer;

/// <summary>
/// Blazor component that displays and manages files and folders within a named content collection.
/// Supports browsing, uploading, downloading, creating folders, and deleting items.
/// All collection I/O is observable end-to-end: the leaves run on the collection's
/// <c>IIoPool</c> — never on this circuit's thread — and the component merely subscribes.
/// A slow SMB/blob store can therefore never block the circuit (the "files disappeared"
/// SignalR flapping this replaced).
/// </summary>
public partial class FileBrowser : IDisposable
{
    [Inject] private IDialogService DialogService { get; set; } = null!;
    private IContentService ContentService => Hub.ServiceProvider.GetRequiredService<IContentService>();
    [Inject] private IToastService ToastService { get; set; } = null!;
    [Inject] private IMessageHub Hub { get; set; } = null!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = null!;
    /// <summary>Name of the content collection to browse. Changing this value reloads the collection.</summary>
    [Parameter] public string? CollectionName { get; set; } = "";
    /// <summary>Current folder path within the collection. Defaults to the root.</summary>
    [Parameter] public string? CurrentPath { get; set; } = "/";
    /// <summary>The highest-level path the browser allows navigating to; prevents navigation above this folder.</summary>
    [Parameter] public string TopLevelPath { get; set; } = "";
    /// <summary>When true the browser renders in an embedded (compact) mode without a full-page chrome.</summary>
    [Parameter] public bool Embed { get; set; }
    /// <summary>
    /// Base URL the embedded browser mirrors its folder position under (e.g. <c>/{node}/Files</c>).
    /// When set, folder rows and breadcrumbs are real links to <c>{UrlBasePath}{folderPath}</c> —
    /// the address bar carries the full sub-folder path and deep links / refresh land in the right
    /// folder. When null, embedded navigation stays in-place (dialogs and other URL-less hosts).
    /// </summary>
    [Parameter] public string? UrlBasePath { get; set; }
    /// <summary>When true, automatically creates <c>CurrentPath</c> inside the collection on initialization if it does not exist.</summary>
    [Parameter] public bool CreatePath { get; set; }
    /// <summary>When true, shows the "New Article" action in the toolbar.</summary>
    [Parameter] public bool ShowNewArticle { get; set; }
    /// <summary>Optional configuration registered with the content service on initialization, creating the collection if it does not already exist.</summary>
    [Parameter] public ContentCollectionConfig? CollectionConfiguration { get; set; }
    /// <summary>Mesh address used to build download and preview URLs for files in the collection.</summary>
    [Parameter] public Address? Address { get; set; }
    /// <summary>When true, hides upload, create, and delete controls so the browser is read-only.</summary>
    [Parameter] public bool IsReadOnly { get; set; }
    private IReadOnlyCollection<CollectionItem> CollectionItems { get; set; } = [];
    FluentInputFile myFileByStream = default!;
    private IDisposable? loadSubscription;

    /// <summary>
    /// Runs after each parameter update: registers any new collection configuration, then
    /// (re)subscribes the load pipeline — resolve the collection, optionally create
    /// <c>CurrentPath</c>, list the items. Everything I/O runs on the collection's pool; this
    /// method only wires the subscription and returns.
    /// </summary>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        // Initialize collection if configuration is provided and collection doesn't exist
        if (CollectionConfiguration is not null)
            ContentService.AddConfiguration(CollectionConfiguration);

        RefreshContent();
    }

    private const string Root = "/";

    /// <summary>
    /// (Re)subscribes the resolve → ensure-folder → list pipeline and pushes the result into the
    /// rendered state. Replaces any in-flight load (the previous subscription is disposed).
    /// </summary>
    private void RefreshContent()
    {
        CurrentPath ??= Root;
        loadSubscription?.Dispose();
        if (CollectionName is null)
        {
            Collection = null;
            CollectionItems = [];
            SelectedItems = [];
            return;
        }

        var name = CollectionName;
        loadSubscription = ContentService.GetCollection(name)
            .SelectMany(collection =>
            {
                if (collection is null)
                    return Observable.Return<(ContentCollection? Collection, IList<CollectionItem> Items)>((null, []));
                var ensureFolder = CreatePath && CurrentPath is not null
                    ? collection.CreateFolder(CurrentPath)
                    : Observable.Return(Unit.Default);
                return ensureFolder
                    .SelectMany(_ => collection.GetCollectionItems(CurrentPath!).ToList())
                    .Select(items => ((ContentCollection? Collection, IList<CollectionItem> Items))(collection, items));
            })
            .Subscribe(
                result =>
                {
                    Collection = result.Collection;
                    CollectionItems = [.. result.Items];
                    SelectedItems = [];
                    InvokeAsync(StateHasChanged);
                },
                ex =>
                {
                    ToastService.ShowError($"Error loading collection '{name}': {ex.Message}");
                    InvokeAsync(StateHasChanged);
                });
    }

    private void CollectionChanged(string collection)
    {
        if (collection == CollectionName)
            return;
        CollectionName = collection;
        RefreshContent();
    }

    /// <summary>Disposes the in-flight load subscription with the circuit.</summary>
    public void Dispose()
    {
        loadSubscription?.Dispose();
        loadSubscription = null;
    }

    private ContentCollection? Collection { get; set; }
    private IEnumerable<CollectionItem> SelectedItems { get; set; } = new List<CollectionItem>();


    private async Task AddFolderAsync()
    {
        DialogParameters<CreateFolderModel> parameters = new()
        {
            Title = $"Create Folder",
            PrimaryAction = "Create",
            PrimaryActionEnabled = false,
            SecondaryAction = "Cancel",
            Width = "500px",
            TrapFocus = true,
            Modal = true,
            PreventScroll = true,
        };
        var dialog = await DialogService.ShowDialogAsync<CreateFolderDialog, CreateFolderModel>(new CreateFolderModel(Collection!, CurrentPath!, CollectionItems), parameters);
        var result = await dialog.Result;
        if (!result.Cancelled)
            RefreshContent();
    }
    private Task NewArticleAsync(MouseEventArgs arg)
    {
        throw new NotImplementedException();
    }

    private async Task DeleteAsync()
    {
        DialogParameters<DeleteDialog> parameters = new()
        {
            Title = $"Create Folder",
            PrimaryAction = "Create",
            PrimaryActionEnabled = false,
            SecondaryAction = "Cancel",
            Width = "500px",
            TrapFocus = true,
            Modal = true,
            PreventScroll = true,
        };
        var deleteModel = new DeleteModel(Collection!, SelectedItems);
        var dialog = await DialogService.ShowDialogAsync<DeleteDialog, DeleteModel>(deleteModel, parameters);
        var result = await dialog.Result;
        if (!result.Cancelled)
        {
            RefreshContent();

            // Show errors if any occurred
            if (deleteModel.Errors.Any())
            {
                foreach (var error in deleteModel.Errors)
                {
                    ToastService.ShowError(error);
                }
            }
            else
            {
                var itemCount = deleteModel.ItemsToDelete.Count();
                ToastService.ShowSuccess($"Successfully deleted {itemCount} item{(itemCount > 1 ? "s" : "")}.");
            }
        }
    }
    private string GetLink(CollectionItem item)
    {
        return item switch
        {
            FolderItem folder => GetLink(folder),
            FileItem file => GetLink(file),
            _ => ""
        };
    }

    private string GetLink(FolderItem item)
    {
        return $"/collections/{CollectionName}{item.Path}";
    }

    /// <summary>
    /// URL for a folder position when the embedding host mirrors navigation in the URL:
    /// <c>{UrlBasePath}{folderPath}</c>. <paramref name="folderPath"/> is collection-relative
    /// with a leading slash (the shape <c>FolderItem.Path</c> and the breadcrumb
    /// accumulator produce).
    /// </summary>
    private string GetUrlLink(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || folderPath == Root)
            return UrlBasePath!;
        return folderPath.StartsWith('/') ? $"{UrlBasePath}{folderPath}" : $"{UrlBasePath}/{folderPath}";
    }
    private string GetLink(FileItem item)
    {
        // Use full address string (e.g., "Cornerstone/Microsoft/2026") for URL generation
        // Both /static and /content endpoints use IMeshCatalog.ResolvePathAsync for dynamic resolution
        var addressString = Address?.ToString();

        // Encode slashes in collection name as ~ to avoid URL path parsing issues
        // e.g., "Submissions@Microsoft/2026" becomes "Submissions@Microsoft~2026"
        var encodedCollection = EncodeCollectionName(CollectionName ?? "");

        if (ShouldDownload(item.Name))
        {
            // Download files: /static/{address}/{encodedCollection}{item.Path}?download
            return $"/static/{addressString}/{encodedCollection}{item.Path}?download";
        }

        // URL-mirrored host: files live in the same collection-named URL space as folders —
        // /{node}/{collection}/{p1}/{p2}/file.ext (the collection-named layout area renders them).
        if (!string.IsNullOrEmpty(UrlBasePath))
            return GetUrlLink(item.Path);

        // Previewable files: /content/{address}/{encodedCollection}{item.Path}
        return $"/content/{addressString}/{encodedCollection}{item.Path}";
    }

    /// <summary>
    /// Encodes a collection name for use in URLs by replacing '/' with '~'.
    /// This avoids issues with ASP.NET Core URL-decoding %2F before route matching.
    /// </summary>
    private static string EncodeCollectionName(string collectionName)
        => ContentCollectionsExtensions.EncodeCollectionName(collectionName);

    /// <summary>
    /// Decodes a collection name from a URL by replacing '~' back to '/'.
    /// </summary>
    internal static string DecodeCollectionName(string encodedName)
        => ContentCollectionsExtensions.DecodeCollectionName(encodedName);

    private static bool ShouldDownload(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            // Documents with converter support are previewed, not downloaded
            ".docx" => false,
            // Other binary formats remain download-only
            ".xlsx" or ".xls" or ".doc" or ".pptx" or ".ppt" or ".zip" => true,
            _ => false
        };
    }

    int progressPercent;
    string progressTitle = "";

    private void OnFileUploaded(FluentInputFileEventArgs file)
    {
        progressPercent = file.ProgressPercent;
        progressTitle = file.ProgressTitle;
        if (Collection is null || file.LocalFile is null)
            return;

        var fileName = file.Name;
        var tempPath = file.LocalFile.FullName;
        // The upload was buffered to a local temp file (InputFileMode.SaveToTemporaryFolder).
        // The factory overload opens it INSIDE the pool leaf — no I/O on the circuit, no await:
        // subscribe-only, per the reactive rules.
        Collection.SaveFile(CurrentPath!, fileName, () => File.OpenRead(tempPath))
            .Finally(() => TryDeleteTempFile(tempPath))
            .Subscribe(
                _ => { },
                ex => InvokeAsync(() =>
                {
                    ToastService.ShowError($"Error uploading {fileName}: {ex.Message}");
                    StateHasChanged();
                }),
                () => InvokeAsync(() =>
                {
                    progressPercent = 100;

                    // Post-upload seam: raise the SAME observer the MCP upload path raises
                    // (MeshOperations.Upload → hub.RaiseContentUploaded) so a GUI upload
                    // auto-indexes like any other upload. Fire-and-forget by contract — the
                    // indexing runs as its own Activity and never blocks the upload (#170).
                    // CurrentPath can carry '\' separators on Windows (the Embed breadcrumb
                    // builds it via Path.Combine) — normalize so the seam always gets the
                    // MCP shape (forward-slash, collection-relative, no leading slash).
                    var folder = (CurrentPath ?? "").Replace('\\', '/').Trim('/');
                    var relativePath = string.IsNullOrEmpty(folder) ? fileName : $"{folder}/{fileName}";
                    Hub.RaiseContentUploaded(QualifiedCollectionPath, relativePath);

                    ToastService.ShowSuccess($"File {fileName} successfully uploaded.");
                    RefreshContent();
                }));
    }

    private void TryDeleteTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (IOException)
        {
            // Best-effort temp-file cleanup; the OS temp folder reclaims leftovers.
        }
    }

    /// <summary>
    /// The node-qualified collection path (<c>{address}/{collection}</c>) that upload
    /// observers expect — the same shape <c>MeshOperations.Upload</c> raises and the same
    /// composition <see cref="GetLink(FileItem)"/> uses for download URLs. Falls back to
    /// <see cref="CollectionName"/> as-is when no <see cref="Address"/> is set or the name
    /// is already qualified with it.
    /// </summary>
    private string QualifiedCollectionPath
    {
        get
        {
            var address = Address?.ToString();
            return string.IsNullOrEmpty(address)
                   || CollectionName!.StartsWith(address + "/", StringComparison.OrdinalIgnoreCase)
                ? CollectionName!
                : $"{address}/{CollectionName}";
        }
    }

    private void OnCompleted(IEnumerable<FluentInputFileEventArgs> files)
    {
        progressPercent = 0;
        progressTitle = "";
        RefreshContent();
    }

    private void ChangePath(string path)
    {
        CurrentPath = path;
        RefreshContent();
    }

    private async Task HandleFileClick(FileItem file)
    {
        // For files that should be downloaded, trigger download via JavaScript
        if (ShouldDownload(file.Name))
        {
            var downloadUrl = GetLink(file);
            await JSRuntime.InvokeVoidAsync("open", downloadUrl, "_blank");
        }
        // For displayable files, let the href handle navigation naturally
        // (preventDefault is false, so navigation happens)
    }

    private async Task DownloadSelectedAsync()
    {
        var filesToDownload = SelectedItems.OfType<FileItem>().ToList();

        if (!filesToDownload.Any())
        {
            ToastService.ShowWarning("Please select at least one file to download.");
            return;
        }

        try
        {
            // Use full address string for URL generation
            var addressString = Address?.ToString();

            // Encode slashes in collection name as ~ to avoid URL path parsing issues
            var encodedCollection = EncodeCollectionName(CollectionName ?? "");

            // Download each file by opening it in a new window/tab
            // The browser will handle the download based on the content-disposition header
            foreach (var file in filesToDownload)
            {
                var downloadUrl = $"/static/{addressString}/{encodedCollection}{file.Path}?download";
                await JSRuntime.InvokeVoidAsync("open", downloadUrl, "_blank");
                // Add a small delay between downloads to avoid browser blocking multiple downloads
                await Task.Delay(100);
            }

            ToastService.ShowSuccess($"Downloading {filesToDownload.Count} file{(filesToDownload.Count > 1 ? "s" : "")}...");

            // Clear selection after download
            SelectedItems = [];
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Error downloading files: {ex.Message}");
        }
    }
}
