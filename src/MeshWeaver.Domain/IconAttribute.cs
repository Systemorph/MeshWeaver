namespace MeshWeaver.Domain;

[AttributeUsage(AttributeTargets.Class)]
public class IconAttribute(string Provider, string Id) : Attribute
{
    public string Provider = Provider;
    public string Id = Id;
}
