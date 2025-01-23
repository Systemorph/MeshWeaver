namespace MeshWeaver.Layout;

[AttributeUsage(AttributeTargets.Property)]
public class UiControlAttribute(Type control) : Attribute
{
    public Type ControlType = control;
    public object Options;
}
public class UiControlAttribute<T>() : UiControlAttribute(typeof(T))
    where T : UiControl
{
}

