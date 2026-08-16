namespace MeshWeaver.AI;

/// <summary>
/// The mesh queries every THREAD LIST binds to — the side panel's thread picker, the in-thread
/// navigation menu, and any list of threads a user can pick from. One place, because the two rules
/// below are invisible at the call site and every hand-written copy has got at least one of them
/// wrong, each time producing the same symptom: a list that renders "No threads yet" forever.
///
/// <para>🚨 <b>Ownership is decided by the QUERY, never re-filtered in the client.</b> Threads live in
/// the <c>_Thread</c> satellite table, and the authorship columns exist ONLY on <c>mesh_nodes</c> —
/// a satellite read projects <c>NULL::text AS created_by</c>
/// (<c>PostgreSqlStorageAdapter.AuthorCols</c>). So <see cref="MeshWeaver.Mesh.MeshNode.CreatedBy"/>
/// is structurally null on EVERY thread that comes back, and a client-side <c>n.CreatedBy == me</c>
/// pass drops every row — for every user, silently. The creator is carried in the thread's CONTENT
/// (<see cref="Thread.CreatedBy"/>), which is where these queries filter, matching the dashboard's
/// cross-partition thread query (<c>CrossPartitionThreadQueryTests</c>).</para>
///
/// <para>🚨 <b>"My threads" is not scoped to the page I am on.</b> A thread is created at
/// <c>{contextPath}/_Thread/{id}</c> for whatever node it was started from, so scoping the list to the
/// current page's node path answers empty everywhere except that one node. RLS already limits the
/// snapshot to what the caller may read; the user filter does the rest.</para>
///
/// <para>The projection always NAMES <c>content</c>: a <c>select:</c> that omits it yields nodes whose
/// <c>Content</c> is silently null (<c>SyncedQueryProjectionContractTest</c>), which would strip the
/// status the lists filter on and the summary they render.</para>
/// </summary>
public static class ThreadQueries
{
    /// <summary>
    /// The columns a thread list needs. <c>content</c> is named deliberately — see the type remarks.
    /// </summary>
    public const string ListProjection =
        "select:path,id,namespace,name,description,nodeType,icon,order,createdBy,lastModified,content";

    private const string NewestFirst = "sort:LastModified-desc";

    /// <summary>
    /// Every thread <paramref name="userId"/> created, across every partition, newest first —
    /// the "Threads" picker. A null/empty user falls back to "every thread I may read".
    /// </summary>
    /// <param name="userId">The current user's id (<c>CircuitUser.ResolveUserId</c>).</param>
    public static string MyThreads(string? userId) =>
        $"nodeType:{ThreadNodeType.NodeType}{OwnedBy(userId)} {NewestFirst} {ListProjection}";

    /// <summary>
    /// <see cref="MyThreads"/> minus the ones marked done — the nav menu's "other open threads".
    /// </summary>
    /// <param name="userId">The current user's id.</param>
    public static string MyOpenThreads(string? userId) =>
        $"nodeType:{ThreadNodeType.NodeType}{OwnedBy(userId)} -content.status:{ThreadExecutionStatus.Done} " +
        $"{NewestFirst} {ListProjection}";

    /// <summary>
    /// The threads nested under <paramref name="path"/> — a thread's delegated sub-threads. Descendants,
    /// not immediate children: a delegation nests at <c>{threadPath}/{responseMsgId}/{subThreadId}</c>.
    /// The node itself is included by the query and filtered out by the caller.
    /// </summary>
    /// <param name="path">The parent thread's path.</param>
    public static string ThreadsUnder(string path) =>
        $"namespace:{path} scope:descendants nodeType:{ThreadNodeType.NodeType} {NewestFirst} {ListProjection}";

    /// <summary>
    /// The ownership term, or nothing when the caller has no user id (RLS still scopes the read).
    /// Filters where the creator actually lives — the thread's content; see the type remarks.
    /// </summary>
    private static string OwnedBy(string? userId) =>
        string.IsNullOrEmpty(userId) ? string.Empty : $" content.createdBy:{userId}";
}
