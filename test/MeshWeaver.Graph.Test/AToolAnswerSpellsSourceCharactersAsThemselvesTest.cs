using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.AI;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A tool answer must spell the source the way the source spells it</b> (MeshWeaver.Plugins#2804).
///
/// <para><b>The incident.</b> The internal pull-request reviewer reads a PR's unified diff with ONE
/// <c>get</c> of its triage item. That answer was written with the hub's serializer options, whose
/// default encoder is HTML-safe, so every <c>+</c> in the diff — the added-line marker AND the
/// operator — reached the model as <c>\u002B</c>. Three review rounds on Plugins#2461 reported the
/// file's only two <c>+=</c> lines as plain assignments, one as a BLOCKING finding that held the PR;
/// the same spelling sits under rebutted blockers on core (<c>failures +=</c>, <c>names +=</c>,
/// <c>/// &lt;summary&gt;</c> read as a stray end tag).</para>
///
/// <para><b>Controls.</b> The same node serialized with the hub's options is asserted to carry the
/// escapes — so the text below really exercises the encoder and a pass is not vacuous; and the
/// answer is parsed back and compared to the stored value, so the relaxed spelling is proved to be
/// a change of SPELLING only, never of content.</para>
/// </summary>
public class AToolAnswerSpellsSourceCharactersAsThemselvesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A slice of the very hunk that was misread, plus the other characters the HTML-safe
    /// encoder rewrites (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c>, <c>`</c>, non-ASCII).</summary>
    private const string Diff =
        "@@ -0,0 +1,3 @@\n"
        + "+        runs += json.loads(out or \"{}\").get(\"workflow_runs\", [])\n"
        + "+    /// <summary>a && b, 'quoted', `ticked` — ü</summary>\n"
        + "+        failures += 0 if ok else 1\n";

    [Fact(Timeout = 120_000)]
    public async Task Get_WritesADiffsPlusSignsAndAngleBrackets_AsThemselves()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode("ToolAnswerSpellingProbe", TestPartition)
            {
                Name = "spelling probe",
                NodeType = "Markdown",
                Description = Diff,
            })
            .FirstAsync()
            .Await(ct);

        // Control: the hub's own options DO escape this text — otherwise the assertions below
        // would pass on any encoder and measure nothing.
        var hubSpelling = JsonSerializer.Serialize(created, Mesh.JsonSerializerOptions);
        hubSpelling.Should().Contain("\\u002B=",
            "the hub's default (HTML-safe) encoder is what spelled `+=` as `\\u002B=` to the reviewer");

        var answer = await new MeshOperations(Mesh).Get("@" + created.Path)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("get must answer for a node that was just created", ct);
        Output.WriteLine($"DIAG get = {answer}");

        answer.Should().Contain("runs += json.loads(out or \\\"{}\\\")",
            "the reviewer must read the operator the file holds — `+=`, not `\\u002B=`");
        answer.Should().Contain("+        failures += 0 if ok else 1",
            "the added-line marker and the operator are both `+`, spelled as such");
        answer.Should().Contain("/// <summary>a && b, 'quoted', `ticked` — ü</summary>");
        answer.Should().NotContain("\\u002B").And.NotContain("\\u003C").And.NotContain("\\u0026");

        JsonNode.Parse(answer)!["description"]!.GetValue<string>().Should().Be(Diff,
            "the relaxed encoder changes how characters are SPELLED, never what the answer says");
    }

    [Fact]
    public void ToolAnswerJson_ParsesToTheSameDocument_AsTheHubsSerialization()
    {
        var node = new MeshNode("Shape", TestPartition) { Name = "a + b < c", NodeType = "Markdown", Description = Diff };
        var options = Mesh.JsonSerializerOptions;

        var relaxed = ToolAnswerJson.Serialize(node, options);
        var hub = JsonSerializer.Serialize(node, options);

        JsonNode.DeepEquals(JsonNode.Parse(relaxed), JsonNode.Parse(hub)).Should().BeTrue(
            "only the spelling differs — same members, same values, same $type");
        relaxed.Should().NotContain("\\u002B");
        ToolAnswerJson.Write(JsonNode.Parse(hub)!).Should().Be(relaxed,
            "the DOM writer and the serializer write the same answer");
    }
}
