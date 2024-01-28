namespace OpenSmc.Arithmetics
{
    /// <summary>
    /// Marks a property, that should be ignored by MapOverFields.MapOver{T}(string,double,T) ( 2 * prop = null)
    /// </summary>
    /// <conceptualLink target="799cb1b4-2638-49fb-827a-43131d364f06" />
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#mapOverFields" />
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class DoNotCopyAttribute : Attribute
    {
    }
}