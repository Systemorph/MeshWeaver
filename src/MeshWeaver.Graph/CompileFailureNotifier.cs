using System.Reactive;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// The graph-side delivery for <see cref="ICompileFailureNotifier"/>: relays a parked NodeType's
/// compile failure into <see cref="NotificationService"/> exactly as
/// <c>NodeTypeCompileParkRegistry</c> used to call it directly — same arguments, same
/// <c>NotificationType.System</c> category, same <c>"system"</c> author, same cold observable.
/// </summary>
internal sealed class CompileFailureNotifier : ICompileFailureNotifier
{
    /// <inheritdoc />
    public IObservable<Unit> NotifyCompileFailed(
        IMessageHub hub,
        string? recipient,
        string mainNodePath,
        string title,
        string message,
        string targetNodePath)
        => NotifyCompileFailed(hub, recipient, mainNodePath,
            LocalizableText.Verbatim(title), LocalizableText.Verbatim(message), targetNodePath);

    /// <inheritdoc />
    /// <remarks>Overridden rather than left on the interface default, so a keyed title/body reaches
    /// the stored row instead of being flattened to its English fallback.</remarks>
    public IObservable<Unit> NotifyCompileFailed(
        IMessageHub hub,
        string? recipient,
        string mainNodePath,
        LocalizableText title,
        LocalizableText message,
        string targetNodePath)
        => NotificationService.DispatchLocalizable(
            hub,
            recipient: recipient,
            mainNodePath: mainNodePath,
            title: title,
            message: message,
            type: NotificationType.System,
            targetNodePath: targetNodePath,
            createdBy: "system");
}
