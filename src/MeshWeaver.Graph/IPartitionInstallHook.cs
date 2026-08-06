using System.Reactive;

namespace MeshWeaver.Graph;

/// <summary>
/// A side effect that must run when a package's content lands in a partition — install, update, or
/// the startup REPAIR pass over already-installed packages.
///
/// <para><b>Why this exists.</b> A package ships content into a partition of its own
/// (<c>Essentials/Agent</c>, <c>Feedback/Agent</c>, …), but the registries that surface that content
/// — the agent picker, the skill menu — resolve from per-user source lists. Nothing connected the
/// two, so a package installed AFTER a user's settings were written stayed invisible to that user
/// forever: the picker asked for <c>namespace:Agent</c>, every agent lived in
/// <c>Essentials/Agent</c>, and the result was an empty picker with no error anywhere.</para>
///
/// <para>The hook is keyed on the PARTITION rather than the manifest so it can live here, in the
/// project both <c>MeshWeaver.PluginCatalog</c> (which installs) and <c>MeshWeaver.AI</c> (which owns
/// the registries) already reference. Neither references the other, and this keeps it that way.</para>
///
/// <para><b>Implementations must be idempotent.</b> They run on every install, every update, and on
/// every startup repair — "already correct" is the common case and must be a no-op, not a rewrite.</para>
/// </summary>
public interface IPartitionInstallHook
{
    /// <summary>
    /// Reconciles whatever this hook owns for content now present in <paramref name="partition"/>.
    /// Cold — the work runs on Subscribe. Errors are the caller's to log; a failing hook must never
    /// fail the install itself, since the content is already written by the time hooks run.
    /// </summary>
    /// <param name="partition">The partition the package installed into, e.g. <c>Essentials</c>.</param>
    IObservable<Unit> OnPartitionInstalled(string partition);
}
