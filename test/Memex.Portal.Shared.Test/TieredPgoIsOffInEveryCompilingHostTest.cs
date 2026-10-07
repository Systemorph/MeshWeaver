using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #890: a process that compiles in-mesh C# must run with Tiered PGO OFF.
///
/// <para>On .NET 10 linux-x64 the Tier-1 JIT, in roughly one test host in three of this suite's
/// workload, inlines Roslyn's <c>SourceMemberContainerTypeSymbol.ContainingType</c>
/// (<c>_containingSymbol as NamedTypeSymbol</c>) into <c>NamedTypeSymbol.AsNestedTypeDefinitionImpl</c>
/// without the <c>isinst</c>. A top-level type's namespace container then reads as its containing type,
/// and every later <c>Emit</c> in that process throws in <c>get_ContainingTypeDefinition</c> — which is
/// how <c>InstallNeverInheritsRepoCompileVerdictTest</c>, <c>ModulesUpdateIndependentlyOfThePlatformTest</c>
/// and <c>ModuleLinkVersionTest</c> went red on unrelated pull requests. Measured in CI's own shape: the
/// listing has no <c>isinst</c> in every poisoned host and has it in every clean one, and with Tiered PGO
/// off no host poisoned (Doc/Architecture/NodeTypeCompilation, "The defect, in one listing").</para>
///
/// <para>The defect itself is per-process and architecture-specific, so it cannot be reproduced on
/// demand in one test. What CAN be pinned deterministically is that the switch that removes it reaches
/// the host: the runtime reads <c>System.Runtime.TieredPGO</c> from this host's runtimeconfig, and the
/// build writes it there from <c>&lt;TieredPGO&gt;false&lt;/TieredPGO&gt;</c> in the Directory.Build.props
/// files. Negative control: building this project with <c>-p:TieredPGO=true</c> makes both assertions
/// fail.</para>
/// </summary>
public class TieredPgoIsOffInEveryCompilingHostTest
{
    private const string Switch = "System.Runtime.TieredPGO";

    [Fact]
    public void TheRuntimeIsHandedTieredPgoOff()
    {
        var value = AppContext.GetData(Switch);
        Assert.True(value is not null,
            $"{Switch} is not set for this host, so the runtime's default (Tiered PGO ON) applies and the "
            + "#890 miscompile can poison every Emit in the process. Set <TieredPGO>false</TieredPGO>.");
        Assert.Equal("false", value!.ToString(), ignoreCase: true);
    }

    [Fact]
    public void TheRuntimeConfigOnDiskCarriesIt()
    {
        var name = typeof(TieredPgoIsOffInEveryCompilingHostTest).Assembly.GetName().Name;
        var path = Path.Combine(AppContext.BaseDirectory, $"{name}.runtimeconfig.json");
        Assert.True(File.Exists(path), $"no runtimeconfig at {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(
            doc.RootElement.GetProperty("runtimeOptions").TryGetProperty("configProperties", out var props)
            && props.TryGetProperty(Switch, out var pgo)
            && pgo.ValueKind == JsonValueKind.False,
            $"{path} does not carry \"{Switch}\": false — the switch never reaches a host launched as "
            + "`dotnet <name>.dll`, which is how CI's shards launch it.");
    }
}
