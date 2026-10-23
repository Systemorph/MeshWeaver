namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// WHY a release create did not land, as a CLASS a reader can act on — the value of
/// <see cref="NodeTypeDefinition.UnreleasedBuildFailureClass"/> (#7072).
///
/// <para>🚨 An OPEN vocabulary of string constants, never an enum (policy
/// <c>open-vocabulary-string-constants</c>): the field is persisted on every NodeType record, so an
/// enum deserialising a value it does not know would fall to its zero member and claim a class it
/// never measured. A value not listed here is UNKNOWN and is acted on by nothing.</para>
///
/// <para>The class decides what the mesh does next, which is why it is stamped rather than parsed
/// out of <see cref="NodeTypeDefinition.UnreleasedBuildReason"/>: only <see cref="Unavailable"/> is
/// re-issued on the activation that holds the stamp, and only once the store is evidenced to answer
/// again — a refusal re-issued would be a retry loop on a verdict. See
/// <c>Doc/Architecture/ReleaseFailureClasses</c>.</para>
/// </summary>
public static class ReleaseFailureClass
{
    /// <summary>
    /// The create was NOT ATTEMPTED: the store could not be reached, or the create was answered
    /// <c>Unavailable</c> for another not-evaluated reason. Nothing was written, and re-issuing the
    /// same create at the same id is meaningful once the store answers again.
    /// </summary>
    public const string Unavailable = "Unavailable";

    /// <summary>
    /// The settle stopped WAITING before an answer came. The create may still land, and the landing
    /// is adopted when it does (<c>LateReleaseAdoption.Landings</c>).
    /// </summary>
    public const string Expired = "Expired";

    /// <summary>The create was judged and REFUSED. Re-issuing it would be refused again.</summary>
    public const string Refused = "Refused";

    /// <summary>The create failed for a reason none of the classes above names.</summary>
    public const string Faulted = "Faulted";
}
