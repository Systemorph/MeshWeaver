using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The hosted registry's OWN workload identity (Systemorph/Memex#922): <c>registry.workloadIdentityClientId</c>
/// reads from the record's JSON in the chart's camelCase shape and is written back unchanged, and a
/// record that states none — every record before this field — reads it as null, so the reader
/// renders exactly what it rendered before.
/// </summary>
public class RegistryIdentityRecordTest
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";

    private const string WithIdentity = """
        {
          "$type": "DeploymentContent",
          "host": "portal.example.com",
          "registry": {
            "host": "registry.example.com",
            "serviceAccount": "memex-registry-sa",
            "workloadIdentityClientId": "11111111-2222-3333-4444-555555555555"
          }
        }
        """;

    private const string WithoutIdentity = """
        {
          "$type": "DeploymentContent",
          "host": "portal.example.com",
          "registry": { "host": "registry.example.com", "serviceAccount": "memex-portal-sa" }
        }
        """;

    [Fact]
    public void TheRegistrysOwnIdentity_ReadsFromTheRecord_AndIsWrittenBack()
    {
        var record = DeploymentRecordJson.Read(WithIdentity)!;
        Assert.Equal(ClientId, record.Registry!.WorkloadIdentityClientId);
        Assert.Equal("memex-registry-sa", record.Registry.ServiceAccount);

        var written = DeploymentRecordJson.Write(record);
        Assert.Contains($"\"workloadIdentityClientId\":\"{ClientId}\"", written);
        Assert.Equal(ClientId, DeploymentRecordJson.Read(written)!.Registry!.WorkloadIdentityClientId);
    }

    [Fact]
    public void ARecordStatingNoIdentity_ReadsNull_AndWritesNoKey()
    {
        var record = DeploymentRecordJson.Read(WithoutIdentity)!;
        Assert.Null(record.Registry!.WorkloadIdentityClientId);
        Assert.DoesNotContain("workloadIdentityClientId", DeploymentRecordJson.Write(record));
    }
}
