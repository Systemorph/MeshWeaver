using Microsoft.CodeAnalysis;

namespace MeshWeaver.CSharp.Roslyn
{
    public class CSharpCompilationException : Exception
    {
        public CSharpCompilationException()
        {
        }


        public CSharpCompilationException(string message) : base(message)
        {
        }

        public CSharpCompilationException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public CSharpCompilationException(string message, IList<Diagnostic> diagnostics) 
            : base($"{message}{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}")
        {
            Diagnostics = new List<Diagnostic>(diagnostics);
        }

        public IList<Diagnostic> Diagnostics { get; }
    }
}