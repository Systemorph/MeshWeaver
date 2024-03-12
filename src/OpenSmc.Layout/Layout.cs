using System.Text.Json.Serialization;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout;

// TODO consider whether we should subsctructure the namespace, e.g. separate out layout etc. ==> should probably be in its own DLL OpenSmc.Application.Layout.Contract

public record SetContextMenuEvent(object View, object Header);

public record ModalDialogOptions(string Size, bool IsClosable)
{
    public ModalDialogOptions()
        :this(Sizes.Medium, false)
    {
    }

    public static class Sizes
    {
        public const string Medium = "M";
        public const string Large = "L";
        public const string Small = "S";
    }
}
public record OpenModalDialogEvent(object View, Func<ModalDialogOptions, ModalDialogOptions> ModalConfig);
public record CloseModalDialogEvent;


public record SetAreaRequest(string Area) : IRequest<LayoutArea>
{
    public SetAreaRequest(string Area, string Path)
    :this(Area)
    {
        this.Path = Path;
    }


    public string Path { get; init; }

    public SetAreaRequest(string Area, object View)
    :this(Area)
    {
        this.View = View;
    }


    public SetAreaRequest(string Area, ViewDefinition ViewDefinition)
    :this(Area)
    {
        this.ViewDefinition = ViewDefinition;
    }

    [JsonIgnore]
    public ViewDefinition ViewDefinition { get; init; }

    public object View { get; init; }

    public IMessageDelivery ForwardedRequest { get; init; }
}


public record RefreshRequest(string Area = "") : IRequest<RefreshResponse>
{
    // TODO SMCv2: consider making this Dictionary<string, object> (2023-10-18, Andrei Sirotenko)
    public object Options { get; init; }
}

public record RefreshResponse(EntityReference Reference);


public delegate object ViewDefinition(RefreshRequest request);

public abstract record ViewElement(string Area);

public record ViewElementWithViewDefinition(string Area, ViewDefinition ViewDefinition) : ViewElement(Area);
public record ViewElementWithView(string Area, object View) : ViewElement(Area);



