#pragma warning disable CS1591

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins <see cref="PackageInstaller.AsAuthored"/>: an install writes the content the FILE declares
/// whenever the parse materialised a RUNTIME-COMPILED content type.
///
/// <para>🚨 A dynamically-compiled content type's <c>$type</c> discriminator is its bare CLR name,
/// and that name is unique only inside its own package — one customer node repo ships
/// <c>Currency</c> in four packages. The deserialiser therefore hands the installer whichever
/// package's record the mesh-wide map last learned, and materialising it is destructive both ways:
/// members the foreign record does not declare are dropped (an installed sample cedent lost its
/// entire content), and defaults it does declare are injected. The unchanged-check then compares an
/// authored file against a foreign materialisation and rewrites the node on every install, with the
/// rewritten set varying run to run (Systemorph/MeshWeaver#1299).</para>
/// </summary>
public class InstallAuthoredContentTest
{
    /// <summary>A content type in a COLLECTIBLE assembly — the shape a compiled NodeType produces
    /// and the only shape whose name-based resolution is a guess.</summary>
    private static object EmitCollectibleContent()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("ClaimsDeepfield_Currency"), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("ClaimsDeepfield_Currency")
            .DefineType("Currency", TypeAttributes.Public | TypeAttributes.Class)
            .CreateType();
        return Activator.CreateInstance(type)!;
    }

    private static PackageFile File(string json) =>
        new("Ifrs17/Currency/CHF.json", json);

    private const string AuthoredJson =
        """{"$type":"MeshNode","id":"CHF","nodeType":"Ifrs17/Currency","content":{"$type":"Currency","code":"CHF"}}""";

    [Fact(Timeout = 30000)]
    public void RuntimeCompiledContent_IsReplacedByTheAuthoredElement()
    {
        var parsed = new MeshNode("CHF", "Ifrs17/Currency") { Content = EmitCollectibleContent() };

        var authored = PackageInstaller.AsAuthored(parsed, File(AuthoredJson), null);

        authored.Content.Should().BeOfType<JsonElement>(
            "a runtime-compiled content type can only have been resolved by a name that is not " +
            "unique across packages — the install must write what the file says");
        ((JsonElement)authored.Content!).GetProperty("code").GetString().Should().Be("CHF",
            "the authored members must survive, including ones the materialised record dropped");
    }

    /// <summary>
    /// Statically-registered content is a real, process-unique registration rather than a name
    /// guess — and the installer's own ordering and compile-trigger logic reads
    /// <c>Content is NodeTypeDefinition</c>, so it must stay typed.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void StaticallyRegisteredContent_StaysTyped()
    {
        var content = new Graph.Configuration.NodeTypeDefinition { Description = "a type" };
        var parsed = new MeshNode("Currency", "Ifrs17") { Content = content };

        PackageInstaller.AsAuthored(parsed, File(AuthoredJson), null).Content
            .Should().BeSameAs(content);
    }

    [Fact(Timeout = 30000)]
    public void ContentlessNode_IsUntouched()
    {
        var parsed = new MeshNode("CHF", "Ifrs17/Currency");

        PackageInstaller.AsAuthored(parsed, File(AuthoredJson), null).Content.Should().BeNull();
    }

    /// <summary>
    /// A file whose authored content cannot be re-read leaves the node exactly as parsed — the
    /// installer never invents a content shape, it only ever prefers the authored one.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void UnreadableAuthoredContent_LeavesTheParsedValue()
    {
        var content = EmitCollectibleContent();
        var parsed = new MeshNode("CHF", "Ifrs17/Currency") { Content = content };

        PackageInstaller.AsAuthored(parsed, File("not json at all"), null).Content
            .Should().BeSameAs(content);
    }
}
