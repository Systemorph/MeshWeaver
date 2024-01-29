using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application;
using OpenSmc.Application.Scope;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit.Abstractions;

namespace OpenSmc.Layout.Test;

public class DataBindingTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddLayout(def => def
                .WithGenerator(request => request.Area == "1", request => Controls.TextBox("1")))
            .WithServices(services =>
                services.Configure<ApplicationAddressOptions>(options =>
                    options.Address = new ApplicationAddress("1", "test")));

    }


     

}