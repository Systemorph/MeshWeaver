namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Produces a natural-language description of an IMAGE file — the vision counterpart to
/// <see cref="ITextExtractor"/>. An image carries no extractable text, so without a describer it
/// indexes to nothing; with one, its description becomes the file's indexable content (chunked +
/// embedded for search) AND its <c>Document</c> summary. Owned by the storage-agnostic indexing core
/// as a thin abstraction so the core takes NO heavy AI dependency — the real, multimodal
/// <c>IChatClient</c>-backed implementation lives in a hosting/AI project; tests supply a fake.
/// </summary>
/// <remarks>
/// Reactive contract (no <c>async</c>/<c>await</c>/<c>Task</c>): the real implementation wraps a single
/// multimodal chat-completion call through <c>IIoPool.Invoke(...)</c> — ONE I/O-pool leaf that takes
/// its OWN slot. <see cref="ContentIndexingService"/> composes it with <c>.SelectMany</c> and never
/// awaits it. Called at most ONCE per image. Emits a single description string (empty when the model
/// is unavailable — the file then indexes as no-text, exactly today's behavior) then completes.
/// </remarks>
public interface IImageDescriber
{
    /// <summary>
    /// File extensions this describer handles (e.g. <c>.png</c>, <c>.jpg</c>). The indexing service
    /// routes a file to the describer instead of the text extractor when its extension is in this set.
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>
    /// Describes the image in <paramref name="imageBytes"/>. <paramref name="fileName"/> is a prompt
    /// hint (name / format cue). Emits exactly one description then completes; emits empty when no
    /// vision model is available so the caller degrades to no-text.
    /// </summary>
    IObservable<string> Describe(byte[] imageBytes, string fileName);
}
