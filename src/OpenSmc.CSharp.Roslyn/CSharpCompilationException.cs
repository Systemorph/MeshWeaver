using System.Runtime.Serialization;
using Microsoft.CodeAnalysis;

namespace OpenSmc.CSharp.Roslyn
{
    public class CSharpCompilationException : Exception
    {
        public CSharpCompilationException()
        {
        }

        protected CSharpCompilationException(SerializationInfo info, StreamingContext context) : base(info, context)
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