using OpenSmc.Layout;
using OpenSmc.Messaging;

namespace OpenSmc.Application.Layout.Views;

public record ExpandRequest(string Area) :  IRequest<AreaChangedEvent>
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
