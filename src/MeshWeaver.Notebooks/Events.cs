using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Notebooks;

// Message types for network communication
public abstract record KernelMessage;
public abstract record RequestMessage;
public abstract record ReplyMessage;
public record ExecuteRequest(string Code, string KernelName) : RequestMessage, IRequest<ExecuteReply>;
public record ResultMessage(object Result) : ReplyMessage;
public record ErrorMessage(string Error) : ReplyMessage;
// Add these message types at the top
public record CompletionRequestMessage(string Code, int Position) : KernelMessage;
public record CompletionResponseMessage(IEnumerable<string> Completions) : KernelMessage;

public record KernelInfoReply(string Language, string LanguageVersion) : ReplyMessage;
public record KernelInfoRequest : KernelMessage, IRequest<KernelInfoReply>;

public record ExecuteReply : ReplyMessage
{
    public string Status { get; }

    public int ExecutionCount { get; }

    public ExecuteReply(string status = null, int executionCount = 0)
    {
        Status = status;
        ExecutionCount = executionCount;
    }

}
public record ExecuteReplyOk : ExecuteReply
{

    public IReadOnlyList<IReadOnlyDictionary<string, string>> Payload { get; init; } = [];

    public ImmutableDictionary<string, string> UserExpressions { get; init; } = ImmutableDictionary<string, string>.Empty;
}
public record ExecuteReplyError : ExecuteReply
{

    public string ErrorName { get; init; }

    public string ErrorValue { get; init; }

    public IReadOnlyList<string> Traceback { get; init; } = [];
}
