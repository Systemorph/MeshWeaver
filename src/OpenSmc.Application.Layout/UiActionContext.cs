using OpenSmc.Messaging;

namespace OpenSmc.Application.Layout;

public interface IUiActionContext
{
    public object Payload { get; }
    public IMessageHub Hub { get; }
}

public record UiActionContext(object Payload, IMessageHub Hub) : IUiActionContext;
