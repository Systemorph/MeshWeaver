using Microsoft.CodeAnalysis;

namespace MeshWeaver.CSharp.Roslyn
{
    public interface ICompiler : IDisposable
    {
        Compilation CreateSubmission(CSharpScript script, CSharpScriptOptions options);

        Func<object[], Task<T>> CreateExecutor<T>(Compilation compilation, CSharpScriptOptions options, in CancellationToken cancellationToken);
    }
}