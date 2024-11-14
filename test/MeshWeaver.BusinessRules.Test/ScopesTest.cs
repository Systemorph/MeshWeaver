using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.BusinessRules.Test;

public class ScopesTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddBusinessRules();
    }
}
