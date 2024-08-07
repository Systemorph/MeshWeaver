using System.Reflection;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.CSharp.Roslyn;

/// <summary>
/// <see cref="MetadataReference"/> does not release unmanaged resources. So this class serves as a reference container with finalizer for proper disposing.
/// </summary>
public class MetadataReferenceWrap(MetadataReference value) : IDisposable
{
    public readonly MetadataReference Value = value;

    protected bool Equals(MetadataReferenceWrap other)
    {
        return Equals(Value, other.Value);
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((MetadataReferenceWrap)obj);
    }

    public override int GetHashCode()
    {
        return (Value != null ? Value.GetHashCode() : 0);
    }

    private void ReleaseUnmanagedResources()
    {
        var type = Value.GetType();
        var getMetadataImpl = type.GetMethod("GetMetadataImpl", BindingFlags.Instance | BindingFlags.NonPublic);
        var metadata = getMetadataImpl?.Invoke(Value, new object[0]) as AssemblyMetadata;
        metadata?.Dispose();
    }

    public void Dispose()
    {
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    ~MetadataReferenceWrap()
    {
        ReleaseUnmanagedResources();
    }

    public override string ToString()
    {
        return $"{{{this.Value?.Display}}}";
    }
}