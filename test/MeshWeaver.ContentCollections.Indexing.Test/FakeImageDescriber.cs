using System.Reactive.Linq;
using MeshWeaver.ContentCollections.Indexing;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// Deterministic in-process <see cref="IImageDescriber"/> for tests: "describes" any image by echoing
/// a fixed sentence that embeds the file name, so a test can assert the exact description that flowed
/// into the chunk store and the Document summary. Counts calls so a test can prove the describer ran
/// (once) for an image and the text summarizer did NOT. No network, no AI dependency.
/// </summary>
public sealed class FakeImageDescriber : IImageDescriber
{
    private int _calls;

    /// <summary>Number of times <see cref="Describe"/> was invoked (subscribed).</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Handles the common raster image extensions.</summary>
    public IReadOnlyCollection<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    public IObservable<string> Describe(byte[] imageBytes, string fileName) =>
        Observable.Defer(() =>
        {
            Interlocked.Increment(ref _calls);
            return Observable.Return(Expected(fileName));
        });

    /// <summary>The exact description this fake produces for <paramref name="fileName"/>.</summary>
    public static string Expected(string fileName) =>
        $"A chart titled quarterly revenue by region, from {fileName}.";
}
