using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Articles;

public abstract class ArticleCollection(string collection)
{
    public string Collection { get; } = collection;
    public abstract IObservable<MeshArticle> GetArticle(string path, ArticleOptions options = null);

    public abstract Task InitializeAsync(CancellationToken ct);
}

public class FileSystemCollection(string collection, string basePath) : ArticleCollection(collection)
{
    private readonly ConcurrentDictionary<string, ReplaySubject<MeshArticle>> articleSubjects = new();
    public string BasePath { get; } = basePath;


    public override async Task InitializeAsync(CancellationToken ct)
    {
        if (articleSubjects.Count > 0)
            return;
        var files = Directory.GetFiles(BasePath, "*.md");
        foreach (var file in files)
        {
            var path = Path.GetRelativePath(BasePath, file);
            var s = articleSubjects.GetOrAdd(path, _ => new(1));
            s.OnNext(await LoadArticle(file));
        }
        MonitorFileSystem();
    }

    public override IObservable<MeshArticle> GetArticle(string path, ArticleOptions options)
    {
        var ret = articleSubjects.GetValueOrDefault(path);
        if (ret is null)
            return null;
        if (options is not null && (!options.ContentIncluded || !options.PrerenderIncluded))
            return ret.Select(art => art with
            {
                Content = options.ContentIncluded ? art.Content : null,
                PrerenderedHtml = options.PrerenderIncluded ? art.PrerenderedHtml : null
            });
        return ret;
    }

    public void MonitorFileSystem()
    {
        var watcher = new FileSystemWatcher(BasePath);
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
        watcher.Changed += OnChanged;
        watcher.EnableRaisingEvents = true;
    }


    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var path = Path.GetRelativePath(BasePath, e.FullPath);
        var s = articleSubjects.GetOrAdd(path, _ => new(1));
        LoadAndSet(e.FullPath, s);
    }

    private async void LoadAndSet(string fullPath, ReplaySubject<MeshArticle> s)
    {
        s.OnNext(await LoadArticle(fullPath));

    }

    private async Task<MeshArticle> LoadArticle(string fullPath)
    {
        if(!File.Exists(fullPath))
           return null;
        await using var stream = File.OpenRead(fullPath);
        var content = await new StreamReader(stream).ReadToEndAsync();
        return ArticleExtensions.ParseArticle(Collection, Path.GetRelativePath(BasePath, fullPath), content);
    }
}
