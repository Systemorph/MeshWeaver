namespace MeshWeaver.Arithmetics
{
    /// <summary>
    /// This attribute can be put on top of <code>IdentityPropertyAttribute</code> in order to aggregate over this property.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class AggregateOverAttribute : Attribute
    {
    }
}