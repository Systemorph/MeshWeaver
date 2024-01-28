using OpenSmc.Messaging;

namespace OpenSmc.Layout.Views;

//not relevant for MVP
public record ExpandControl(object Collapsed) : ExpandableUiControl<ExpandControl, ExpandableUiPlugin<ExpandControl>>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);

public class ExpandableUiPlugin<TControl> : GenericUiControlPlugin<TControl>, IMessageHandlerAsync<ExpandRequest>
    where TControl : UiControl
{

    public Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery<ExpandRequest> delivery)
    {
        return ExpandAsync(delivery);
    }


    protected virtual async Task<IMessageDelivery> ExpandAsync(IMessageDelivery<ExpandRequest> delivery)
    {
        var expandableUiControl = (IExpandableUiControl)State;
        if (expandableUiControl.ExpandFunc == null)
            return delivery.Ignored();
        var view = await expandableUiControl.ExpandFunc.Invoke(new UiActionContext(delivery.Message.Payload, Hub));
        var response = new AreaChangedEvent(delivery.Message.Area, view);
        Post(response, o => o.ResponseFor(delivery));
        return delivery.Processed();
    }

    protected ExpandableUiPlugin(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }
}