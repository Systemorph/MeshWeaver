namespace MeshWeaver.Mesh.Security;

/// <summary>
/// 🚨 THE ONE PREDICATE for whether the permissions a principal HOLDS meet a permission DEMAND.
/// Every evaluator of a demanded <see cref="Permission"/> — the hub's permission check, the access
/// gate, row-level security, the delete pre-flight, the menu — asks <see cref="IsMetBy"/>, never a
/// bare <c>HasFlag</c>, so the meaning of a demand is decided in one place.
///
/// <para>A demand means ALL of its bits, with ONE named exception: <see cref="UpdateOrDelete"/>,
/// the demand deleting ordinary content makes (<c>DeletePermission.RequiredFor</c>). Either bit
/// suffices: an <c>Editor</c> deletes content with Update, and a role that carries Delete keeps the
/// right to delete content it always had, whether or not it also carries Update (policy
/// <c>delete-suffices-for-content</c>). No demand needs Update and Delete together, so the combined
/// value is free to carry this meaning; a reader who wants both must ask for each separately.</para>
/// </summary>
public static class PermissionDemand
{
    /// <summary>
    /// The demand for deleting ordinary content: <see cref="Permission.Update"/> OR
    /// <see cref="Permission.Delete"/> — ANY ONE of the two is enough.
    /// </summary>
    public const Permission UpdateOrDelete = Permission.Update | Permission.Delete;

    /// <summary>
    /// True when <paramref name="held"/> meets <paramref name="demand"/>: any one bit of
    /// <see cref="UpdateOrDelete"/>, every bit of any other demand. <see cref="Permission.None"/>
    /// is met by anything.
    /// </summary>
    public static bool IsMetBy(this Permission demand, Permission held)
        => demand == UpdateOrDelete
            ? (held & UpdateOrDelete) != Permission.None
            : held.HasFlag(demand);

    /// <summary>
    /// A demand as a person reads it in a refusal: <c>Update or Delete</c> for
    /// <see cref="UpdateOrDelete"/>, the enum's own text for every other demand.
    /// </summary>
    public static string Describe(this Permission demand)
        => demand == UpdateOrDelete
            ? $"{Permission.Update} or {Permission.Delete}"
            : demand.ToString();
}
