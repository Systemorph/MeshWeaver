using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

namespace OpenSmc.Northwind.Application;

public static class HubConfiguration
{
    public static IMessageHub CreateNorthwindHub<TAddress>(
        this IServiceProvider serviceProvider,
        TAddress address
    )
    {
        //TODO Roland Bürgi 2024-05-24: Nees to split better by view model. Need to check how blazor handles such views.
        //I guess this is already local to one user and I won't have to take care. Should be able to steer via DI config.
        //For production scenario, need to factor Northwind hubs out into Orleans grains or similar.
        return serviceProvider.CreateMessageHub(
            address,
            conf => conf.ConfigureNorthwindHubs().AddNorthwindViews()
        );
    }

    public static MessageHubConfiguration ConfigureNorthwindHubs(
        this MessageHubConfiguration configuration
    )
    {
        return configuration.WithRoutes(forward =>
            forward
                .RouteAddressToHostedHub<ReferenceDataAddress>(c => c.AddNorthwindReferenceData())
                .RouteAddressToHostedHub<EmployeeAddress>(c => c.AddNorthwindEmployees())
                .RouteAddressToHostedHub<OrderAddress>(c => c.AddNorthwindOrders())
                .RouteAddressToHostedHub<SupplierAddress>(c => c.AddNorthwindSuppliers())
                .RouteAddressToHostedHub<ProductAddress>(c => c.AddNorthwindProducts())
                .RouteAddressToHostedHub<CustomerAddress>(c => c.AddNorthwindCustomers())
        );
        ;
    }
}
