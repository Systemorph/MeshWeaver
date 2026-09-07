using System.Text.Json;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A policy record that has LOST its <c>policy</c> field must read as
/// <see cref="UpdatePolicyKind.None"/> — never as "auto-update enabled" (#3542, proposal 3).
///
/// <para>The defect this pins is a two-part mechanism, and neither part is visible on its own. The
/// hub serializer sets <c>DefaultIgnoreCondition = WhenWritingDefault</c>, so whichever enum member
/// is ZERO is omitted from the persisted record; and an omitted field deserializes back to that same
/// member. While <c>Continuous</c> was zero, a record that lost its policy under its own bookkeeping
/// writes read back as the MOST PERMISSIVE state, reached purely by losing information. That is how
/// memex-cloud rolled onto a withdrawn <c>3.1.0-ci</c> line "on a policy record that lost its own
/// policy".</para>
///
/// <para>🚨 The two assertions below are a pair on purpose. Fail-closed alone would be satisfied by
/// an enum nobody can express Continuous in; round-tripping alone would be satisfied by the old
/// order. Together they say: an ABSENT policy is None, and an EXPLICIT Continuous survives — which
/// is the distinction the old shape could not make.</para>
/// </summary>
public class AbsentUpdatePolicyFailsClosedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public void AnAbsentPolicyFieldReadsAsNone()
    {
        // A record persisted without a `policy` field — exactly what the bookkeeping write leaves
        // behind when the policy is at the serializer's default.
        const string withoutPolicy = """{"latestAvailableTag":"3.0.0-ci.8009"}""";

        var content = JsonSerializer.Deserialize<UpdatePolicyContent>(
            withoutPolicy, Mesh.JsonSerializerOptions);

        Assert.NotNull(content);
        Assert.Equal(UpdatePolicyKind.None, content!.Policy);
    }

    [Fact]
    public void AnExplicitContinuousSurvivesTheRoundTrip()
    {
        // The other half: because None is now the zero value, Continuous is non-default and must be
        // WRITTEN OUT — otherwise an admin's explicit choice would decay into the absent case and be
        // silently downgraded to None on the next read.
        var chosen = new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous };

        var json = JsonSerializer.Serialize(chosen, Mesh.JsonSerializerOptions);
        Assert.Contains("Continuous", json);

        var readBack = JsonSerializer.Deserialize<UpdatePolicyContent>(json, Mesh.JsonSerializerOptions);
        Assert.Equal(UpdatePolicyKind.Continuous, readBack!.Policy);
    }
}
