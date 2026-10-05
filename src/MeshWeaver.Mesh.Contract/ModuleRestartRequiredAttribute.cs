namespace MeshWeaver.Mesh;

/// <summary>
/// The ONLY reasons a module may be restart-required (policy <c>module-live-update-default</c>):
/// genuinely BOOT-TIME infrastructure, which the process binds before anything can be swapped. A
/// deliberately CLOSED list — the guard (<see cref="ModuleLiveUpdateGuard"/>) refuses any other
/// category, because a declaration whose reason is not boot-time infrastructure is a defect to remove
/// (convert the contribution), not a reason to keep restarting.
/// </summary>
public static class ModuleBootCategory
{
    /// <summary>A storage or database driver, or a persistence provider — selected before the mesh exists
    /// and pinned for the life of the process (PostgreSQL, Cosmos, Snowflake, the IO pools and store
    /// modules pinned at process start).</summary>
    public const string StorageDriver = "StorageDriver";

    /// <summary>Orleans / silo-level registration — grain types, silo configuration.</summary>
    public const string Orleans = "Orleans";

    /// <summary>Authentication and token infrastructure — the schemes and validators the host's
    /// middleware is built around.</summary>
    public const string Authentication = "Authentication";

    /// <summary>The platform's own host.</summary>
    public const string Host = "Host";

    /// <summary>Every category the guard accepts.</summary>
    public static readonly System.Collections.Immutable.ImmutableHashSet<string> All =
        System.Collections.Immutable.ImmutableHashSet.Create(StringComparer.Ordinal, StorageDriver, Orleans, Authentication, Host);
}

/// <summary>
/// 🚨 The EXPLICIT, justified exception to live update (policy <c>module-live-update-default</c>,
/// <c>Doc/Architecture/LiveModuleUpdate</c>): a module that is genuinely BOOT-TIME infrastructure
/// declares it on its own assembly — <c>[assembly: ModuleRestartRequired(ModuleBootCategory.StorageDriver,
/// "selected by Graph:Storage:Type before the mesh is built")]</c>.
///
/// <para>Live is the default and needs no declaration. The category must be one of
/// <see cref="ModuleBootCategory"/>; anything else fails the guard. A declared module's live swap is
/// refused BEFORE anything is loaded, the running generation keeps serving, and the automatic,
/// approval-free restart activates the new one.</para>
/// </summary>
/// <param name="category">One of <see cref="ModuleBootCategory"/>.</param>
/// <param name="reason">Why, in this module's own words. Blank is refused.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ModuleRestartRequiredAttribute(string category, string reason) : Attribute
{
    /// <summary>The boot-time category — one of <see cref="ModuleBootCategory"/>.</summary>
    public string Category { get; } = category;

    /// <summary>Why this module needs a restart to update.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// The guard behind the live-by-default rule: only boot-time infrastructure may be restart-required, and
/// it must say so. Pure over an assembly's materialised contributions.
/// </summary>
public static class ModuleLiveUpdateGuard
{
    /// <summary>
    /// Why <paramref name="contributions"/> violate the rule, or null when they do not:
    /// <list type="bullet">
    /// <item>a declaration whose category is not a <see cref="ModuleBootCategory"/> — a defect to remove:
    /// the module is not boot-time infrastructure, so its contributions must be converted;</item>
    /// <item>a declaration with a blank reason;</item>
    /// <item>a module whose contributions cannot be re-applied in-process and that carries no valid
    /// declaration — the message names every measured blocker, which is the list of conversions owed.</item>
    /// </list>
    /// </summary>
    /// <param name="moduleName">The module's entry-assembly name.</param>
    /// <param name="contributions">What it contributes.</param>
    public static string? Violation(string moduleName, ModuleContributions contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        if (contributions.DeclaredRestartCategory is { } category)
        {
            if (!ModuleBootCategory.All.Contains(category))
                return $"{moduleName} declares [ModuleRestartRequired] for '{category}', which is not boot-time infrastructure "
                       + $"({string.Join(", ", ModuleBootCategory.All.Order(StringComparer.Ordinal))}) — remove the declaration "
                       + "and convert the contribution so the module updates live";
            if (string.IsNullOrWhiteSpace(contributions.DeclaredRestartReason))
                return $"{moduleName} declares [ModuleRestartRequired] with a BLANK reason — an unexplained exception to live update is not a declaration";
            return null;
        }
        var measured = contributions.MeasuredLiveUpdateBlockers();
        return measured.IsEmpty
            ? null
            : $"{moduleName} cannot be updated live — convert: {string.Join("; ", measured)}. Only boot-time infrastructure "
              + "may instead declare [assembly: MeshWeaver.Mesh.ModuleRestartRequired(ModuleBootCategory.<category>, \"<why>\")]";
    }
}
