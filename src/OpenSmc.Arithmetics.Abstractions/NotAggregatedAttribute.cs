namespace OpenSmc.Arithmetics
{
    /// <summary>
    /// Can be explicitly put to mark not aggregated properties.
    /// By default, all numeric types are aggregated.
    /// This attribute can be used on properties of these types to mark them as not aggregated..
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class NotAggregatedAttribute : Attribute
    {
    }
}