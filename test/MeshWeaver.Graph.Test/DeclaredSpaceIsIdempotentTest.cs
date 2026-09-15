using System.Text.Json;
using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A Space DECLARED in a content repo must deserialize to the SAME content every time</b>
/// (#4382). A content type whose property carries a clock or a guid as its initializer is
/// non-idempotent by construction: the declaring file omits the field, every read stamps a fresh
/// value, the incoming content differs from the stored content, and the installer's unchanged-skip
/// can never hold.
///
/// <para>The failure is silent by nature — the re-write produces the CORRECT node, so nothing goes
/// red. The only instrument that ever asked the question was MeshWeaver.Plugins' idempotence gate
/// (<c>re-install of the unchanged snapshot wrote 1 node(s)</c>), and only where a pack declares
/// one. Core's own <c>samples/Graph/Data/{Systemorph,ACME,MeshWeaver}.json</c> declare three, all
/// omitting <c>createdAt</c>, and had been re-written on every import for as long as the default
/// existed.</para>
///
/// <para>This test is the cheap, local form of that gate: parse the same declaration twice and
/// require the two values to be equal.</para>
/// </summary>
public class DeclaredSpaceIsIdempotentTest
{
    /// <summary>A Space exactly as a content repo declares one — no <c>createdAt</c>.</summary>
    private const string Declared = """
        {
          "description": "A tenant container declared by a content repo.",
          "website": "https://example.invalid",
          "isVerified": true
        }
        """;

    [Fact]
    public void TheSameDeclaration_DeserializesToEqualContent_Twice()
    {
        var first = JsonSerializer.Deserialize<Space>(Declared, JsonSerializerOptions.Web)!;
        var second = JsonSerializer.Deserialize<Space>(Declared, JsonSerializerOptions.Web)!;

        Assert.Equal(first, second);
    }

    /// <summary>
    /// The mechanism, named rather than implied: an omitted provenance field stays ABSENT. A
    /// default of <c>DateTimeOffset.UtcNow</c> would make the assertion above fail; a default of
    /// <c>default(DateTimeOffset)</c> would make it pass while writing 0001-01-01 onto every
    /// declared Space — trading an unstable value for a false one. Null is the only reading that
    /// is both stable and true.
    /// </summary>
    [Fact]
    public void AnOmittedCreatedAt_StaysAbsent()
    {
        var space = JsonSerializer.Deserialize<Space>(Declared, JsonSerializerOptions.Web)!;

        Assert.Null(space.CreatedAt);
    }

    /// <summary>A writer that DOES record the timestamp still round-trips it.</summary>
    [Fact]
    public void ADeclaredCreatedAt_IsPreserved()
    {
        const string withStamp = """
            {"description": "d", "createdAt": "2026-01-02T03:04:05+00:00"}
            """;

        var space = JsonSerializer.Deserialize<Space>(withStamp, JsonSerializerOptions.Web)!;

        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"), space.CreatedAt);
    }
}
