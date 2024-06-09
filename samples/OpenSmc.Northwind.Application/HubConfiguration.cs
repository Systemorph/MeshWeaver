using OpenSmc.Blazor;
using OpenSmc.Messaging;
using OpenSmc.Northwind.Model;
using OpenSmc.Northwind.ViewModel;

namespace OpenSmc.Northwind.Application;

public static class HubConfiguration
{
    public static MessageHubConfiguration ConfigureNorthwindHubs(
        this MessageHubConfiguration configuration
    )
    {
        // TODO V10: add pre-compiled statement to remove all northwind related config (05.06.2024, Alexander Kravets)
        return configuration
            .AddBlazor(x => x)
            .AddNorthwindViewModels()
            .AddNorthwindEmployees()
            .AddNorthwindOrders()
            .AddNorthwindSuppliers()
            .AddNorthwindProducts()
            .AddNorthwindCustomers();
        // .WithRoutes(forward =>
        //     forward
        //         .RouteAddressToHostedHub<ReferenceDataAddress>(c =>
        //             c.AddNorthwindReferenceData()
        //         )
        //         .RouteAddressToHostedHub<EmployeeAddress>(c => c.AddNorthwindEmployees())
        //         .RouteAddressToHostedHub<OrderAddress>(c =>    c.AddNorthwindOrders())
        //         .RouteAddressToHostedHub<SupplierAddress>(c => c.AddNorthwindSuppliers())
        //         .RouteAddressToHostedHub<ProductAddress>(c =>  c.AddNorthwindProducts())
        //         .RouteAddressToHostedHub<CustomerAddress>(c => c.AddNorthwindCustomers())
        // );
        ;
    }
}
