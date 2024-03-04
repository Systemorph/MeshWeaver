using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

namespace OpenSmc.Layout;

/// <summary>
/// Use Data property to bind to its area changed. Area is given inside area changed, ==> react to area changed as sent from this address
/// </summary>
public record RemoteViewControl : UiControl<RemoteViewControl, RemoteViewPlugin>, IUiControlWithSubAreas
{

    //needed for serialization
    // ReSharper disable once ConvertToPrimaryConstructor
    public RemoteViewControl()
        : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
                                                               {
                                                               }

    internal Action<IMessageDelivery<DataChangedEvent>, IMessageHub> UpdateFunction { get; init; }
    public RemoteViewControl WithViewUpdate(Action<IMessageDelivery<DataChangedEvent>, IMessageHub> getViewUpdate, Func<LayoutArea, LayoutArea, LayoutArea> updateView) 
        => this with { UpdateFunction = getViewUpdate, UpdateView = updateView };


    #region Remote mode
    public RemoteViewControl(object Message, object RedirectAddress, string RedirectArea)
        : this()
    {
        this.Message = Message;
        this.RedirectAddress = RedirectAddress;
        this.RedirectArea = RedirectArea;
        UpdateFunction = (_, hub) => hub.Post(Message, o => o.WithTarget(RedirectAddress));
    }

    internal object Message { get; init; }
    internal object RedirectAddress { get; init; }
    internal string RedirectArea { get; init; }
    #endregion

    #region application scope mode
    public RemoteViewControl(ViewDefinition viewDefinition, SetAreaOptions options)
        : this()
    {
        ViewDefinition = viewDefinition;
        Options = options;
        UpdateFunction = (_, hub) => hub.Post(new RefreshRequest(View.Area)
                                                    {
                                                        Options = new RemoteViewRefreshOptions(true)
                                                    });
    }

    internal ViewDefinition ViewDefinition { get; init; }
    internal SetAreaOptions Options { get; init; }
    #endregion

    internal LayoutArea View
    {
        get => (LayoutArea)Data;
        init => Data = value;
    }

    IReadOnlyCollection<LayoutArea> IUiControlWithSubAreas.SubAreas => new[] { View };

    IUiControlWithSubAreas IUiControlWithSubAreas.SetArea(LayoutArea areaChanged)
    {
        if (areaChanged.Area != nameof(Data))
            return this;
        return this with { Data = areaChanged };
    }

    internal Func<LayoutArea, LayoutArea, LayoutArea> UpdateView
    {
        get;
        init;
    }

}

public record RemoteViewRefreshOptions(bool ForceRefresh);