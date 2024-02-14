using Microsoft.CodeAnalysis;

namespace OpenSmc.CSharp.Roslyn
{
    public interface ICompiler : IDisposable
    {
        Compilation CreateSubmission(CSharpScript script, CSharpScriptOptions options);

        Func<object[], Task<T>> CreateExecutor<T>(Compilation compilation, CSharpScriptOptions options, in CancellationToken cancellationToken);
    }
}