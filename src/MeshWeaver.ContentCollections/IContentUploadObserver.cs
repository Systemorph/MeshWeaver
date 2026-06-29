using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Post-upload notification seam. An observer is invoked AFTER a file has been successfully written
/// into a content collection (after <c>SaveFileAsync</c>), carrying the qualified collection path and
/// the file's relative path within it.
///
/// <para>This is the ONLY coupling point between content uploads and downstream reactions (indexing,
/// virus-scan, thumbnailing, …). <c>MeshWeaver.ContentCollections</c> only RAISES the event — it takes
/// no dependency on any indexing / pg / AI project. Reactors (e.g. the indexing pipeline in
/// <c>MeshWeaver.ContentCollections.Indexing.Graph</c>) register an implementation; with none
/// registered the upload path is unchanged (no-op).</para>
///
/// <para>An observer must return immediately and do its real work off-band (e.g. by firing an
/// Activity). The upload handler fire-and-forgets it (see <see cref="ContentUploadObserverExtensions.RaiseContentUploaded"/>),
/// so the upload response is never blocked on the reaction.</para>
/// </summary>
public interface IContentUploadObserver
{
    /// <summary>
    /// Called after a file is saved into a content collection. <paramref name="collectionPath"/> is the
    /// qualified collection path (e.g. <c>Systemorph/content</c>); <paramref name="filePath"/> is the
    /// file's path relative to the collection root (e.g. <c>docs/notes.txt</c>).
    /// </summary>
    void OnUploaded(string collectionPath, string filePath);
}

/// <summary>
/// Hub-level helper that raises <see cref="IContentUploadObserver"/> to every registered observer.
/// Resolves observers from the hub's service provider (a no-op when none are registered) and invokes
/// each in a try/catch so one reactor's failure never breaks the upload or another reactor.
/// </summary>
public static class ContentUploadObserverExtensions
{
    /// <summary>
    /// Notifies every registered <see cref="IContentUploadObserver"/> that a file was uploaded. Each
    /// observer is responsible for returning promptly (firing its own off-band work) — this method
    /// invokes them inline but they must NOT block. Best-effort: a throwing observer is logged and
    /// swallowed so the upload still succeeds.
    /// </summary>
    public static void RaiseContentUploaded(this IMessageHub hub, string collectionPath, string filePath)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(collectionPath) || string.IsNullOrEmpty(filePath))
            return;

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.ContentCollections.Upload");

        foreach (var observer in hub.ServiceProvider.GetServices<IContentUploadObserver>())
        {
            try
            {
                observer.OnUploaded(collectionPath, filePath);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Content upload observer {Observer} threw for {Collection}/{File} (best-effort, ignored)",
                    observer.GetType().Name, collectionPath, filePath);
            }
        }
    }
}
