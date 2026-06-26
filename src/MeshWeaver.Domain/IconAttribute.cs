namespace MeshWeaver.Domain;

/// <summary>
/// Associates a class with an icon, identified by an icon provider and an icon id.
/// </summary>
/// <param name="Provider">The icon provider (icon set) the icon belongs to.</param>
/// <param name="Id">The provider-specific identifier of the icon.</param>
[AttributeUsage(AttributeTargets.Class)]
public class IconAttribute(string Provider, string Id) : Attribute
{
    /// <summary>
    /// The icon provider (icon set) the icon belongs to.
    /// </summary>
    public string Provider = Provider;
    /// <summary>
    /// The provider-specific identifier of the icon.
    /// </summary>
    public string Id = Id;
}
