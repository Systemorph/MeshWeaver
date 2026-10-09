using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.ImageClosures;

/// <summary>One image a reader expects a closure record for.</summary>
/// <param name="Repository">The image repository.</param>
/// <param name="Digest">The <c>sha256:</c> digest.</param>
public sealed record ExpectedImage(string Repository, string Digest);

/// <summary>
/// What a reader of <see cref="ImageClosure"/> records actually covered — the statement every gate
/// that depends on the data must print (#4066, policy <c>image-closure-as-mesh-data</c>).
///
/// <para>🚨 A mesh read can answer SMALLER rather than wrong: a record never written, written to a
/// different instance, or outside the reader's reach all look like "no row". So a missing record is
/// <b>NOT MEASURED</b>, never clean, and <see cref="AllMeasured"/> is true only when every expected
/// image was read. Zero expected images is not a verdict either.</para>
/// </summary>
public sealed record ImageClosureCoverage
{
    /// <summary>The images the reader expected, in the order asked.</summary>
    public ImmutableList<ExpectedImage> Expected { get; init; } = ImmutableList<ExpectedImage>.Empty;

    /// <summary>The records found for expected images.</summary>
    public ImmutableList<ImageClosure> Measured { get; init; } = ImmutableList<ImageClosure>.Empty;

    /// <summary>The expected images with no record — each one NOT MEASURED.</summary>
    public ImmutableList<ExpectedImage> NotMeasured { get; init; } = ImmutableList<ExpectedImage>.Empty;

    /// <summary>Where the records were read from.</summary>
    public string Source { get; init; } = ImageClosureNodes.Root;

    /// <summary>True only when at least one image was expected and every one was read.</summary>
    public bool AllMeasured => Expected.Count > 0 && NotMeasured.Count == 0;

    /// <summary>The one-line coverage statement a gate prints with its verdict.</summary>
    public string Statement => Expected.Count == 0
        ? $"image-closure coverage: nothing was asked of {Source} — 0 of 0 is not a verdict"
        : $"image-closure coverage: {Measured.Count} of {Expected.Count} expected image(s) measured from {Source}"
          + (NotMeasured.Count == 0
              ? $" ({Measured.Sum(m => m.FileCount)} file(s) in total)"
              : "; NOT MEASURED: " + string.Join(", ", NotMeasured.Select(e => $"{e.Repository}@{e.Digest}")));

    /// <summary>Pure: the coverage of <paramref name="expected"/> by the records <paramref name="read"/>.</summary>
    /// <param name="expected">The images the reader needs.</param>
    /// <param name="read">The records the reader obtained.</param>
    public static ImageClosureCoverage Of(IEnumerable<ExpectedImage> expected, IEnumerable<ImageClosure> read)
    {
        var want = expected.Distinct().ToImmutableList();
        var byKey = read
            .GroupBy(r => (r.Repository, r.Digest))
            .ToImmutableDictionary(g => g.Key, g => g.First());
        var measured = want
            .Select(e => byKey.TryGetValue((e.Repository, e.Digest), out var r) ? r : null)
            .OfType<ImageClosure>()
            .ToImmutableList();
        return new ImageClosureCoverage
        {
            Expected = want,
            Measured = measured,
            NotMeasured = want.Where(e => !byKey.ContainsKey((e.Repository, e.Digest))).ToImmutableList(),
        };
    }

    /// <summary>
    /// Reads the records for <paramref name="expected"/> as System — one query over exactly the expected
    /// paths (<c>path:a|b|c</c>), so the read's cost is bounded by the question, not by history — and
    /// states the coverage. An index that trails the store can only report an image as NOT MEASURED,
    /// the loud direction; it can never manufacture a record.
    /// </summary>
    /// <param name="hub">The hub whose mesh holds the records.</param>
    /// <param name="expected">The images the reader needs.</param>
    public static IObservable<ImageClosureCoverage> Read(IMessageHub hub, IReadOnlyCollection<ExpectedImage> expected)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        if (expected.Count == 0)
            return Observable.Return(Of(expected, []));
        // ONLY the expected paths (`path:a|b|c`), never the whole namespace: records accumulate one per
        // built image and are ~360 KB each, so a namespace listing grows without bound and would
        // eventually be truncated — reporting a present image as NOT MEASURED.
        var paths = expected.Select(e => ImageClosureNodes.PathOf(e.Repository, e.Digest)).Distinct(StringComparer.Ordinal);
        var query = $"path:{string.Join('|', paths)} nodeType:{ImageClosureNodes.NodeType}";
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query)).Take(1))
            .Select(change => Of(expected, change.Items
                .Select(n => n.ContentAs<ImageClosure>(hub.JsonSerializerOptions))
                .OfType<ImageClosure>()));
    }
}
