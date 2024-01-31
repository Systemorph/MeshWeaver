using OpenSmc.Application.Scope;
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
        : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new AreaChangedEvent(nameof(Data), new SpinnerControl()))
                                                               {
                                                               }

    internal Action<IMessageDelivery<DataChanged>, IMessageHub> UpdateFunction { get; init; }
    public RemoteViewControl WithViewUpdate(Action<IMessageDelivery<DataChanged>, IMessageHub> getViewUpdate, Func<AreaChangedEvent, AreaChangedEvent, AreaChangedEvent> updateView) 
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

    internal AreaChangedEvent View
    {
        get => (AreaChangedEvent)Data;
        init => Data = value;
    }

    IReadOnlyCollection<AreaChangedEvent> IUiControlWithSubAreas.SubAreas => new[] { View };

    IUiControlWithSubAreas IUiControlWithSubAreas.SetArea(AreaChangedEvent areaChanged)
    {
        if (areaChanged.Area != nameof(Data))
            return this;
        return this with { Data = areaChanged };
    }

    public override bool IsUpToDate(object other)
    {
        if (Data == null)
            return base.IsUpToDate(other);

        if (other is not RemoteViewControl remoteView)
            return false;

        return (this with { Data = null }).IsUpToDate(remoteView with { Data = null })
            && IsUpToDate(View, remoteView.View);

    }

    internal Func<AreaChangedEvent, AreaChangedEvent, AreaChangedEvent> UpdateView
    {
        get;
        init;
    }

    protected override MessageHubConfiguration ConfigureHub(MessageHubConfiguration configuration)
    {
        return base.ConfigureHub(configuration).WithForwards
            (
                forward => forward
                    .RouteMessageToTarget<SubscribeToEvaluationRequest>(_ => ExpressionSynchronizationAddress(forward.Hub))
                    .RouteMessageToTarget<UnsubscribeFromEvaluationRequest>(_ => ExpressionSynchronizationAddress(forward.Hub))
                );
    }

    private  ExpressionSynchronizationAddress ExpressionSynchronizationAddress(IMessageHub hub) => LayoutExtensions.ExpressionSynchronizationAddress(hub.Address);
}

public record RemoteViewRefreshOptions(bool ForceRefresh);