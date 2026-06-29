using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
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

    protected IObservable<IReadOnlyCollection<PropertyRisk>> GetPropertyRisks(Address address)
    {
        var hub = Mesh;
        return hub.Observe(
                new GetDataRequest(new CollectionReference(nameof(PropertyRisk))),
                o => o.WithTarget(address))
            .Select(risksResp =>
                (IReadOnlyCollection<PropertyRisk>)((risksResp?.Message?.Data as IEnumerable<object>)?
                    .Select(x => x as PropertyRisk ?? (x as JsonObject)?.Deserialize<PropertyRisk>(hub.JsonSerializerOptions))
                    .Where(x => x != null)
                    .Cast<PropertyRisk>()
                    .ToList()
                    ?? []));
    }

    protected IObservable<IReadOnlyCollection<Pricing>> GetPricings()
    {
        var hub = Mesh;
        return hub.Observe(
                new GetDataRequest(new CollectionReference(nameof(Pricing))),
                o => o.WithTarget(InsuranceApplicationAttribute.Address))
            .Select(pricingsResp =>
                (IReadOnlyCollection<Pricing>)((pricingsResp.Message.Data as InstanceCollection)?
                    .Instances.Values
                    .Select(x => x as Pricing ?? (x as JsonObject)?.Deserialize<Pricing>(hub.JsonSerializerOptions))
                    .Where(x => x != null)
                    .Cast<Pricing>()
                    .ToList()
                    ?? []));
    }
}
