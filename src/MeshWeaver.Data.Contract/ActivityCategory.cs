namespace MeshWeaver.Data;

/// <summary>
/// Well-known category names for an <see cref="ActivityLog"/>, identifying the kind of
/// work that produced the activity.
/// </summary>
public static class ActivityCategory
{
    /// <summary>
    /// Activity arising from a data change (create/update/delete of workspace entities).
    /// </summary>
    public const string DataUpdate = nameof(DataUpdate);
    /// <summary>
    /// Activity arising from an import operation.
    /// </summary>
    public const string Import = nameof(Import);
    /// <summary>
    /// Activity arising from a compilation (e.g. code/business-rule build).
    /// </summary>
    public const string Compilation = nameof(Compilation);
    /// <summary>
    /// Activity whose category is unknown or unclassified.
    /// </summary>
    public const string Unknown = nameof(Unknown);
}

