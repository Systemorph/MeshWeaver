using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.OgCard;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the OgCard module lane: the assembly carries a <see cref="MeshNodeProviderAttribute"/>
/// whose default-node-hub configuration registers the area on EVERY per-node hub (the markdown
/// <c>@@</c> embed resolves it on the embedding document's hub) — listing the DLL under
/// <c>Modules:Assemblies</c> is the complete activation, and delisting removes the server-side
/// external URL-fetch surface.
/// </summary>
public class OgCardModuleTest
{
    [Fact]
    public void TheAssembly_CarriesTheModuleAttribute_WithAnEveryNodeHubRegistration()
    {
        var attributes = typeof(OgCardLayoutArea).Assembly
            .GetCustomAttributes<MeshNodeProviderAttribute>()
            .ToList();
        Assert.NotEmpty(attributes);
        Assert.Contains(attributes, a => a.DefaultNodeHubConfigurations.Any());
    }
}
