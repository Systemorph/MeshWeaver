using System.Reactive;
using MeshWeaver.Data;
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

    /// <summary>
    /// The same emission for text the platform OWNS: the title and body carry their catalog key and
    /// arguments, so the bell renders them in the language of whoever READS the row rather than the
    /// one the compile happened to run under (Systemorph/MeshWeaver#4373). A compile parks with no
    /// viewer in scope, so this is the only shape that can reach a German reader.
    ///
    /// <para>🚨 <b>Default-implemented on purpose.</b> A new interface member obliges every
    /// implementer, and a forwarder cannot rescue one — that is exactly the deadlock
    /// <c>scripts/check-interface-addition.py</c> exists for (#3465). The default forwards to the
    /// string overload, so an implementer outside this repository keeps compiling and keeps
    /// behaving as it did, losing only the key it never had.</para>
    ///
    /// <para><see cref="LocalizableText"/> has no implicit conversion from <see cref="string"/> —
    /// deliberately, so that producing an UNKEYED sentence is always spelled out — which means a
    /// plain string still binds the string overload and no existing call site changes meaning.</para>
    /// </summary>
    /// <param name="hub">The hub the dispatch runs on.</param>
    /// <param name="recipient">The user to notify, or <c>null</c> for the platform operators' bell.</param>
    /// <param name="mainNodePath">The recipient, or the failing type when there is none.</param>
    /// <param name="title">The notification title, with its catalog key when the platform owns it.</param>
    /// <param name="message">The notification body, same shape as <paramref name="title"/>.</param>
    /// <param name="targetNodePath">The failing NodeType's path.</param>
    IObservable<Unit> NotifyCompileFailed(
        IMessageHub hub,
        string? recipient,
        string mainNodePath,
        LocalizableText title,
        LocalizableText message,
        string targetNodePath)
        => NotifyCompileFailed(hub, recipient, mainNodePath, title.English, message.English, targetNodePath);
}
