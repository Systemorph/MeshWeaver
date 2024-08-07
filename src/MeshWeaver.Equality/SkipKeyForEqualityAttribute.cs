namespace MeshWeaver.Equality
{
    //This attribute is used to handle Entity types. When they ll gone, we can kill this attribute
    [AttributeUsage(AttributeTargets.Property)]
    public class SkipKeyForEqualityAttribute : Attribute
    {
    }
}
