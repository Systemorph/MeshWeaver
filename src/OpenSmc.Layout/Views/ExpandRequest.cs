using OpenSmc.Messaging;

namespace OpenSmc.Layout.Views;


public record ClickedEvent(string Area)
{
    public object Payload { get; init; }

};
