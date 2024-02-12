using OpenSmc.Hub.Fixture;
using OpenSmc.Import;
using OpenSmc.Messaging;
using Xunit.Abstractions;

namespace OpenSmc.Import.Test
{
    public class ImportTest(ITestOutputHelper output) : HubTestBase(output)
    {

        protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        {
            return base.ConfigureHost(configuration)
                //.AddImport(import => import.WithFileSource())
                ;
        }
    }
}
