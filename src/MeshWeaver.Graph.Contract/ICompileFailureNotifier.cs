using System.Reactive;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The seam by which the NodeType compile pipeline reports a PARKED terminal compile failure to a
/// person, without depending on the notification model.
///
/// <para>The pipeline (<c>NodeTypeCompileParkRegistry</c>, MeshWeaver.Compiler) knows a type parked
/// and who asked for it; the delivery — bell node, per-user settings, deterministic email — is
/// <c>NotificationService</c> in MeshWeaver.Graph, which reads <c>Notification</c>,
/// <c>NotificationSettings</c> and <c>NotificationRule</c> node types. Calling it directly is what
/// made the compile pipeline depend on the graph model, so the call is inverted through this
/// interface instead: MeshWeaver.Graph registers the implementation in <c>AddGraph</c>, and the
/// pipeline resolves it OPTIONALLY.</para>
///
/// <para>🚨 Optional on purpose, and it must stay optional: a hub composed without <c>AddGraph</c>
/// has no notification model to deliver into. Resolving this as required would turn "no bell" into
/// a faulted compile.</para>
/// </summary>
public interface ICompileFailureNotifier
{
    /// <summary>
    /// Emits the compile-failure notification. Returns a COLD observable — the caller subscribes,
    /// exactly as the direct <c>NotificationService.Dispatch</c> call it replaced did, so the
    /// dispatch keeps running off the caller's subscription rather than eagerly on this call.
    /// </summary>
    /// <param name="hub">The hub the dispatch runs on.</param>
    /// <param name="recipient">The user to notify, or <c>null</c> for a System-driven build — in
    /// which case the notification is made a satellite of the failing type instead.</param>
    /// <param name="mainNodePath">The recipient, or the failing type when there is none.</param>
    /// <param name="title">The notification title.</param>
    /// <param name="message">The notification body.</param>
    /// <param name="targetNodePath">The failing NodeType's path.</param>
    IObservable<Unit> NotifyCompileFailed(
        IMessageHub hub,
        string? recipient,
        string mainNodePath,
        string title,
        string message,
        string targetNodePath);
}
