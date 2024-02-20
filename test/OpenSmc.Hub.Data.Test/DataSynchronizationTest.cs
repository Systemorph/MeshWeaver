using OpenSmc.Data;
using OpenSmc.Data.Domain;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit.Abstractions;
using static OpenSmc.Data.Domain.TestDomain;

namespace OpenSmc.Hub.Data.Test;

public class DataSynchronizationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data => data.WithDataSource("ReferenceData", dataSource => dataSource
                .ConfigureReferenceData()
            ));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddData(
                data => data
                    .WithDataSource("ReferenceData",
                        dataSource => dataSource
                            .WithType<BusinessUnit>()
                    )
            );
    }
}