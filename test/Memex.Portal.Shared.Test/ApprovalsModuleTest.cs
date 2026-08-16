using System.Linq;
using System.Reflection;
using MeshWeaver.Approvals;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the Approvals module lane: the assembly carries a <see cref="MeshNodeProviderAttribute"/>
/// whose builder configuration invokes the same <c>AddApprovals()</c> a fixture calls — node type
/// on the mesh plus the form/inline areas and menu entry on EVERY per-node hub — so listing the
/// DLL under <c>Modules:Assemblies</c> is the complete activation, and delisting removes the
/// Approvals UI mesh-wide while the Approval record and its satellite mapping stay platform-level.
/// </summary>
public class ApprovalsModuleTest
{
    [Fact]
    public void TheAssembly_CarriesTheModuleAttribute_WithABuilderRegistration()
    {
        var attributes = typeof(ApprovalsView).Assembly
            .GetCustomAttributes<MeshNodeProviderAttribute>()
            .ToList();
        Assert.NotEmpty(attributes);
        Assert.Contains(attributes, a => a.BuilderConfigurations.Any());
    }
}
