using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TrackedChange = MeshWeaver.Mesh.TrackedChange;
using TrackedChangeType = MeshWeaver.Mesh.TrackedChangeType;
using TrackedChangeStatus = MeshWeaver.Mesh.TrackedChangeStatus;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for adding tracked change support to a message hub.
/// <para>
/// Tracked changes are satellite nodes in the <c>_Tracking</c> sub-partition. Like comments, a change
/// is NOT woven into the document text: it captures its range/anchor and the diff view is re-derived
/// at render time (see <see cref="ChangeRendering"/> / <see cref="CollaborativeRenderer"/>). Accepting
/// applies the suggested text to the document; rejecting drops the satellite.
/// </para>
/// Comments remain in <c>_Comment</c> via <see cref="CommentsExtensions"/>.
/// </summary>
public static class AnnotationExtensions
{
    /// <summary>
    /// The sub-partition name where tracked changes are stored.
    /// </summary>
    public const string TrackingPartition = "_Tracking";

    /// <summary>
    /// Marker type used to detect if tracking is enabled in a hub configuration.
    /// </summary>
    public record TrackingEnabled;

    /// <summary>
    /// Adds tracked change support to the message hub configuration.
    /// Registers the TrackedChange type under the _Tracking partition and the suggest-edit handler.
    /// Comments are handled separately by AddComments().
    /// </summary>
    public static MessageHubConfiguration AddTracking(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithType<TrackedChange>(nameof(TrackedChange))
            .WithType<CreateSuggestedEditRequest>(nameof(CreateSuggestedEditRequest))
            .WithType<CreateSuggestedEditResponse>(nameof(CreateSuggestedEditResponse))
            .Set(new TrackingEnabled())
            .WithHandler<CreateSuggestedEditRequest>(HandleCreateSuggestedEdit)
            .AddData(data => data.WithDataSource(_ =>
                new MeshDataSource(Guid.NewGuid().AsString(), data.Workspace)
                    .WithType<TrackedChange>(TrackingPartition, nameof(TrackedChange))));
    }

    /// <summary>
    /// Handles a suggested edit by capturing it as a position-anchored satellite — WITHOUT mutating
    /// the document. The diff view is re-derived from the satellite at render time; accepting later
    /// applies the text. Mirrors the comment handler: read the node once for content + version,
    /// create the satellite, never await.
    /// </summary>
    private static IMessageDelivery HandleCreateSuggestedEdit(
        IMessageHub hub,
        IMessageDelivery<CreateSuggestedEditRequest> request)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<TrackingEnabled>>();
        var msg = request.Message;

        try
        {
            var nodePath = hub.Address.ToString();
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var workspace = hub.GetWorkspace();
            var changeId = Guid.NewGuid().ToString("N")[..8];

            workspace.GetMeshNodeStream()
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(5))
                .Catch<MeshNode, Exception>(_ => Observable.Return((MeshNode)null!))
                .SelectMany(node =>
                {
                    var version = node?.Version ?? 0;
                    var clean = node?.Content is MarkdownContent md && !string.IsNullOrEmpty(md.Content)
                        ? MarkdownAnnotationParser.StripAllMarkers(md.Content)
                        : "";

                    var changeType = ChangeRendering.Classify(msg.DeletedText, msg.InsertedText);
                    var start = Math.Clamp(msg.Position, 0, clean.Length);
                    var length = msg.DeletedText?.Length ?? 0;
                    if (start + length > clean.Length)
                        length = Math.Max(0, clean.Length - start);

                    var change = new TrackedChange
                    {
                        Id = changeId,
                        PrimaryNodePath = nodePath,
                        MarkerId = changeId,
                        ChangeType = changeType,
                        Author = msg.Author,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Status = TrackedChangeStatus.Pending,
                        Version = version,
                        Start = start,
                        Length = length,
                        AnchorText = clean,
                        OriginalText = msg.DeletedText,
                        NewText = msg.InsertedText
                    };
                    var changeNode = new MeshNode(changeId, $"{nodePath}/{TrackingPartition}")
                    {
                        Name = $"Suggestion by {msg.Author}",
                        NodeType = TrackedChangeNodeType.NodeType,
                        MainNode = nodePath,
                        Content = change
                    };

                    logger?.LogInformation(
                        "[SuggestEdit] {Id} on {Path}: {Kind} pos={Start}+{Length} v={Version}",
                        changeId, nodePath, changeType, start, length, version);

                    return meshService.CreateNode(changeNode);
                })
                .Subscribe(
                    _ => hub.Post(new CreateSuggestedEditResponse(true, changeId, null), o => o.ResponseFor(request)),
                    ex =>
                    {
                        logger?.LogWarning(ex, "[SuggestEdit] FAILED for {Path}", nodePath);
                        hub.Post(new CreateSuggestedEditResponse(false, null, ex.Message), o => o.ResponseFor(request));
                    });

            return request.Processed();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[SuggestEdit] EXCEPTION on {Path}", hub.Address);
            hub.Post(new CreateSuggestedEditResponse(false, null, ex.Message), o => o.ResponseFor(request));
            return request.Processed();
        }
    }

    /// <summary>
    /// Checks if tracking is enabled in the configuration.
    /// </summary>
    public static bool HasTracking(this MessageHubConfiguration configuration)
        => configuration.Get<TrackingEnabled>() != null;
}
