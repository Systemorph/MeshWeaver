using MeshWeaver.Mesh.Threading;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The modules GC's own pass verdict, beside the shared <see cref="PassOutcome"/> set (which is
/// open on purpose, policy <c>open-vocabulary-string-constants</c>).
/// </summary>
public static class GcPassOutcome
{
    /// <summary>
    /// Some activation entry or set record could not be READ, so the reference set is incomplete
    /// and every generation delete was skipped (#2509). Transient folders still collect, so this
    /// is neither COLLECTED (the plan was not run) nor ABORTED (the pass did run and may have
    /// removed staging or trash directories).
    /// </summary>
    public const string FailedClosed = "FAILED CLOSED";
}
