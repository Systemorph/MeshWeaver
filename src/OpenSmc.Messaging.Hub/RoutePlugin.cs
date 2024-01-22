using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenSmc.Messaging.Hub;

public class RoutePlugin : MessageHubPlugin<RoutePlugin>
{
    private readonly ForwardConfiguration forwardConfiguration;

    public RoutePlugin(IServiceProvider serviceProvider, ForwardConfiguration forwardConfiguration) : base(serviceProvider)
    {
        this.forwardConfiguration = forwardConfiguration;
        Register(ForwardMessageAsync);
    }

    protected override bool Filter(IMessageDelivery d) => d.State == MessageDeliveryState.Submitted;

    private async Task<IMessageDelivery> ForwardMessageAsync(IMessageDelivery delivery)
    {
        foreach (var item in GetForwards(delivery))
            delivery = await item.Route(delivery);
        return delivery;
    }

    private IEnumerable<IForwardConfigurationItem> GetForwards(IMessageDelivery delivery)
    {
        return forwardConfiguration.Items.Where(f => f.Filter(delivery));
    }
}