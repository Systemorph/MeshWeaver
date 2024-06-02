namespace OpenSmc.Layout;

public interface IGenericType
{
    Type BaseType { get; }
    Type[] TypeArguments { get; }
}
