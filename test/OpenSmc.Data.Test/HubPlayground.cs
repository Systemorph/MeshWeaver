using FluentAssertions;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Hub.Data.Test;

public class SynchronizationStreamTest : HubTestBase
{
    protected SynchronizationStreamTest(ITestOutputHelper output)
        : base(output) { }
}
