using System.Collections.Immutable;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using LinePositionSpan = Microsoft.DotNet.Interactive.LinePositionSpan;

namespace OpenSmc.CSharp.Kernel
{
    public class CSharpReturnValueProduced : KernelEvent
    {
        public object Value { get; }
        public bool IsVoid { get; }
        
        public CSharpReturnValueProduced(object value, bool isVoid, KernelCommand command) 
            : base(command)
        {
            Value = value;
            IsVoid = isVoid;
        }
    }
    public class CSharpModuleAddedProduced : KernelEvent
    {
        public string Name { get; }
        public string Version { get; }

        public CSharpModuleAddedProduced(string name, string version, KernelCommand command)
            : base(command)
        {
            Name = name;
            Version = version;
        }
    }

    public class CSharpHoverTextProduced : KernelEvent
    {
        public string Description { get; }
        public string Documentation { get; }
        public LinePositionSpan LinePositionSpan { get; }

        public CSharpHoverTextProduced(string description, string documentation, LinePositionSpan linePositionSpan, KernelCommand command) 
            : base(command)
        {
            Description = description;
            Documentation = documentation;
            LinePositionSpan = linePositionSpan;
        }
    }

    public class CSharpCompletionsProduced : KernelEvent
    {
        public ImmutableArray<CSharpCompletionItem> Completions { get; }
            
        public CSharpCompletionsProduced(IEnumerable<CSharpCompletionItem> completions, KernelCommand command)
            : base(command)
        {
            Completions = completions.ToImmutableArray();
        }
    }
    public class CSharpCommandFailed : KernelEvent
    {
        public Exception Exception { get; }
        public string Message { get; }

        public CSharpCommandFailed(KernelCommand command, Exception exception, string message)
            : base(command)
        {
            Exception = exception;
            Message = message;
        }
    }

    public class CSharpCompletionItem : CompletionItem
    {
        public LinePositionSpan Span { get; }

        public CSharpCompletionItem(string displayText, 
                                              string kind,
                                              LinePositionSpan span,
                                              string filterText = null, 
                                              string sortText = null, 
                                              string insertText = null, 
                                              string documentation = null)
            : base(displayText, kind, filterText, sortText, insertText, documentation: documentation)
        {
            Span = span;
        }
    }
}
