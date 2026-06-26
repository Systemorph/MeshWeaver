namespace MeshWeaver.Domain;

/// <summary>
/// Marks a member with a horizontal alignment hint used when rendering it in the UI.
/// </summary>
public class AlignAttribute : Attribute
{
    /// <summary>
    /// The alignment to apply to the annotated member.
    /// </summary>
    public Align Align;
}

/// <summary>
/// Specifies how content is aligned along an axis.
/// </summary>
public enum Align{
    /// <summary>Align content to the start (left/top) edge.</summary>
    Start,
    /// <summary>Center the content.</summary>
    Center,
    /// <summary>Align content to the end (right/bottom) edge.</summary>
    End}
