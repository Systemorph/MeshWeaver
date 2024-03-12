using OpenSmc.Messaging;

namespace OpenSmc.Layout.Views;

public record ExpandRequest(string Area) :  IRequest<LayoutArea>
{
    public object Payload { get; init; }
}

public record ClickedEvent
{
    public object Payload { get; init; }

    public object Options { get; init; }

    public ClickedEvent() {}
    public ClickedEvent(object payload) => Payload = payload;
};
