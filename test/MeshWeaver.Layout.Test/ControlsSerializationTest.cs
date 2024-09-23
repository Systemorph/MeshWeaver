using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Hub.Fixture;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;

public class ControlsSerializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration).AddLayout(x => x);
    }

    private const string benchmark =
        "{\"data\":\"Hello World\",\"moduleName\":\"" + ModuleSetup.ModuleName +"\",\"apiVersion\":\"" + ModuleSetup.ApiVersion + "\",\"skins\":[],\"$type\":\"MeshWeaver.Layout.HtmlControl\"}";

    [HubFact]
    public  void BasicSerialization()
    {
        var host = GetHost();
        var serialized = JsonSerializer.Serialize(new HtmlControl("Hello World"), host.JsonSerializerOptions);
        serialized.Should().Be(benchmark);


    }
}
