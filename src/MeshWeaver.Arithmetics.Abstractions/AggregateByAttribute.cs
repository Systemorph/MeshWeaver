namespace MeshWeaver.Arithmetics
{
    /// <summary>
    /// This attribute can be put on top of properties in order to behave like a property with <code>IdentityPropertyAttribute</code> for aggregation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class AggregateByAttribute : Attribute
    {
    }
}
