namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 The EXPLICIT, justified exception to live update (policy <c>module-live-update-default</c>,
/// <c>Doc/Architecture/LiveModuleUpdate</c>): a module that cannot be swapped inside the running
/// process DECLARES it, on its own assembly, with the reason —
/// <c>[assembly: ModuleRestartRequired("binds root services the container builds once")]</c>.
///
/// <para>Live is the default and needs no declaration. A module whose contributions the platform
/// MEASURES as not re-appliable (<see cref="ModuleContributions.LiveUpdateBlockers"/>) must carry
/// this attribute — the guard (<see cref="ModuleLiveUpdateGuard"/>) fails a module that blocks a live
/// swap without it — and a module may also declare a cause the measurement cannot see (process-wide
/// static state, a thread it owns). Either way the live swap is refused BEFORE anything is loaded,
/// the running generation keeps serving, and the automatic, approval-free restart activates the new
/// one.</para>
/// </summary>
/// <param name="reason">Why this module cannot be updated without a restart — shown wherever the
/// restart is explained. Blank is refused: an unexplained exception is not a declaration.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ModuleRestartRequiredAttribute(string reason) : Attribute
{
    /// <summary>Why this module needs a restart to update.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// The guard behind the live-by-default rule: a module that blocks a live swap must SAY so. Pure over
/// an assembly's materialised contributions.
/// </summary>
public static class ModuleLiveUpdateGuard
{
    /// <summary>
    /// Why <paramref name="contributions"/> violate the rule, or null when they do not: a module
    /// whose contributions cannot be re-applied in-process and that carries no
    /// <see cref="ModuleRestartRequiredAttribute"/>, or that carries one with a blank reason. The
    /// message names the module and every measured blocker, so the fix — move the contribution to a
    /// re-appliable surface, or declare the exception with its reason — is in the failure itself.
    /// </summary>
    /// <param name="moduleName">The module's entry-assembly name.</param>
    /// <param name="contributions">What it contributes.</param>
    public static string? Violation(string moduleName, ModuleContributions contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        if (contributions.DeclaredRestartReason is { } declared)
            return string.IsNullOrWhiteSpace(declared)
                ? $"{moduleName} declares [ModuleRestartRequired] with a BLANK reason — an unexplained exception to live update is not a declaration"
                : null;
        var measured = contributions.MeasuredLiveUpdateBlockers();
        return measured.IsEmpty
            ? null
            : $"{moduleName} cannot be updated live and does not declare it — add "
              + "[assembly: MeshWeaver.Mesh.ModuleRestartRequired(\"<why>\")] or move the contribution to a re-appliable "
              + $"surface. Measured: {string.Join("; ", measured)}";
    }
}
