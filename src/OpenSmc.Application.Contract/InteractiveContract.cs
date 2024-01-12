using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Application;

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


public record SetAreaRequest : IRequest<AreaChangedEvent>
{
    public SetAreaRequest(string area, string Path)
    {
        this.Path = Path;
        Options = new(area);
    }

    public string Area => Options.Area;

    public string Path { get; init; }

    public SetAreaRequest(SetAreaOptions Options, object View)
    {
        this.View = View;
        this.Options = Options;
    }

    public SetAreaRequest()
    {
    }

    public SetAreaOptions Options { get; init; }

    public SetAreaRequest(SetAreaOptions Options, ViewDefinition ViewDefinition)
    {
        this.ViewDefinition = ViewDefinition;
        this.Options = Options;
    }

    [JsonIgnore]
    public ViewDefinition ViewDefinition { get; init; }

    public object View { get; init; }

    public IMessageDelivery ForwardedRequest { get; init; }
}

public record AreaChangedEvent
{
    public AreaChangedEvent(string area, object view)
        : this(area, view, null, null)
    {
    }
    public AreaChangedEvent(string area, object view, object Options)
        : this(area, view, Options, null)
    {
    }

    [Newtonsoft.Json.JsonConstructor]
    public AreaChangedEvent(string area, object view, object options, IReadOnlyCollection<ViewDependency> dependencies)
    {
        Area = area;
        View = view;
        Options = options;
        Dependencies = dependencies;
    }

    public string Area { get; init; }
    public object Options { get; init; }
    public object View { get; init; }
    public IReadOnlyCollection<ViewDependency> Dependencies { get; init; }
}

public record ViewDependency(string ScopeId, string Property);

public record RefreshRequest : IRequest<AreaChangedEvent>
{
    public RefreshRequest(string Area = "")
    {
        this.Area = Area;
    }

    // TODO SMCv2: consider making this Dictionary<string, object> (2023-10-18, Andrei Sirotenko)
    public object Options { get; init; }
    public string Area { get; init; }

}
