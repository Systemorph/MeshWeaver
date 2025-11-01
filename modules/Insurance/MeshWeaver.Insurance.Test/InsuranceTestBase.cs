using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Insurance.Test;

public abstract class InsuranceTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureHub(c => c
                .AddData()
            )
            .InstallAssemblies(typeof(InsuranceApplicationAttribute).Assembly.Location);
    }

    protected async Task<IReadOnlyCollection<PropertyRisk>> GetPropertyRisksAsync(PricingAddress address)
    {
        var hub = Mesh;
        var risksResp = await hub.AwaitResponse(
            new GetDataRequest(new CollectionReference(nameof(PropertyRisk))),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        return (risksResp?.Message?.Data as IEnumerable<object>)?
            .Select(x => x as PropertyRisk ?? (x as JsonObject)?.Deserialize<PropertyRisk>(hub.JsonSerializerOptions))
            .Where(x => x != null)
            .Cast<PropertyRisk>()
            .ToList()
            ?? [];
    }

    protected async Task<IReadOnlyCollection<Pricing>> GetPricingsAsync()
    {
        var hub = Mesh;
        var pricingsResp = await hub.AwaitResponse(
            new GetDataRequest(new CollectionReference(nameof(Pricing))),
            o => o.WithTarget(InsuranceApplicationAttribute.Address),
            new CancellationTokenSource(10.Seconds()).Token);

        return (pricingsResp.Message.Data as InstanceCollection)?
               .Instances.Values
               .Select(x => x as Pricing ?? (x as JsonObject)?.Deserialize<Pricing>(hub.JsonSerializerOptions))
            .Where(x => x != null)
            .Cast<Pricing>()
            .ToList()
            ?? [];
    }
}
