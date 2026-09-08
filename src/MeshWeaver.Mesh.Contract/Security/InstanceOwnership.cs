namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Who an instance belongs to, as the person registering it stated — the ownership record the
/// registry keeps against the id it issues.
///
/// <para>🚨 <b>This is NOT the same as the key's owner, and conflating them is why it exists.</b> On
/// the bootstrap and open lanes <c>MeshWeaverInstanceService.Register</c> fills
/// <c>OwnerUserName</c>/<c>OwnerUserEmail</c> from the person who MINTED the registration key — a
/// platform admin at the registry, who is very often not the person standing up the instance. For an
/// open registration in particular the registrant is the owner and the key's owner is a stranger to
/// them. So a stated ownership record WINS where it is given, and where it is not the existing
/// fallback is untouched.</para>
///
/// <para>Every field is optional at this layer: the registry accepts what it is told. Whether the
/// fields are REQUIRED is a decision for the surface collecting them — the first-run wizard demands
/// all three plus consent before it will register at all, because "collect ownership, then get the
/// id and credentials" is the order the requirement asks for.</para>
/// </summary>
public sealed record InstanceOwnership
{
    /// <summary>The organisation this instance belongs to.</summary>
    public string? Company { get; init; }

    /// <summary>The name of the person who registered it.</summary>
    public string? Name { get; init; }

    /// <summary>The email of the person who registered it — how the registry reaches the owner about
    /// the id it has issued them.</summary>
    public string? Email { get; init; }

    /// <summary>Whether this record states anything at all. An ownership record with every field
    /// blank is indistinguishable from none, and must not override a fallback with emptiness.</summary>
    public bool IsStated =>
        !string.IsNullOrWhiteSpace(Company)
        || !string.IsNullOrWhiteSpace(Name)
        || !string.IsNullOrWhiteSpace(Email);

    /// <summary>
    /// <paramref name="stated"/> when it says something, else <paramref name="fallback"/>.
    ///
    /// <para>Per FIELD, not per record: a registrant who gives a company and a name but no email
    /// should keep the fallback email rather than lose it to a blank.</para>
    /// </summary>
    /// <param name="stated">What the registrant supplied.</param>
    /// <param name="fallback">What the lane already had — typically the key owner's.</param>
    public static string Prefer(string? stated, string fallback) =>
        string.IsNullOrWhiteSpace(stated) ? fallback : stated.Trim();
}
