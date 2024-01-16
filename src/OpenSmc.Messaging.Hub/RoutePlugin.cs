using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenSmc.Messaging.Hub;

public class RoutePlugin : MessageHubPlugin<RoutePlugin>
{
    private readonly IMessageHub parentHub;
    private readonly ForwardConfiguration forwardConfiguration;

    public RoutePlugin(IServiceProvider serviceProvider, IMessageHub parentHub) : base(serviceProvider)
    {
        this.parentHub = parentHub;
        Register(ForwardMessageAsync);
    }

    private async Task<IMessageDelivery> ForwardMessageAsync(IMessageDelivery delivery)
    {
        foreach (var item in GetForwards(delivery))
            delivery = await item.Route(delivery);
        if(parentHub != null)
            return parentHub.DeliverMessage(delivery);
        return delivery;
    }

    private IEnumerable<IForwardConfigurationItem> GetForwards(IMessageDelivery delivery)
    {
        return forwardConfiguration.Items.Where(f => f.Filter(delivery));
    }

}