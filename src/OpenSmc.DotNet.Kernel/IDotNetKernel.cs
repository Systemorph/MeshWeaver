using System.Diagnostics.CodeAnalysis;

namespace OpenSmc.DotNet.Kernel;

public interface IDotNetKernel
{
    void AddUsings([NotNull] params string[] namespaces);
    void AddUsingsStatic([NotNull] params string[] staticNamespaces);
    void AddUsingType<T>(string name = null);
}