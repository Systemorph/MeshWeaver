namespace OpenSmc.Data;

public interface IWorkspaceMessage
{
    WorkspaceReference Reference { get; }
    object Message { get; }
}

public record WorkspaceMessage<TMessage>(WorkspaceReference Reference, TMessage Message)
    : IWorkspaceMessage
{
    object IWorkspaceMessage.Message => Message;
}
