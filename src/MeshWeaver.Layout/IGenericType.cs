namespace MeshWeaver.Layout;

public interface IGenericType
{
    Type BaseType { get; }
    Type[] TypeArguments { get; }
}
