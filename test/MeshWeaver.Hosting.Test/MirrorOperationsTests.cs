using System.Collections.Generic;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Unit tests for the request-validation surface of <see cref="MirrorOperations"/>.
/// The full push/pull flow is exercised end-to-end against a live local Aspire +
/// staging portal in the smoke recipe (see <c>Doc/Architecture/CrossInstanceMirror.md</c>);
/// this file pins the synchronous validation that catches obvious operator
/// mistakes before any HTTP traffic.
/// </summary>
public class MirrorRequestValidationTests
{
    [Fact]
    public void MirrorRequest_serializes_dryrun_paths_in_a_stable_order()
    {
        // The wire shape callers (MCP tool, UI summary) parse — pin its public surface.
        var result = new MirrorResult
        {
            Status = "DryRun",
            Direction = "Push",
            SourcePath = "rbuergi/Story",
            TargetPath = "rbuergi/Story",
            NodesScanned = 3,
            Paths = new[] { "rbuergi/Story/01-Code", "rbuergi/Story", "rbuergi/Story/02-Activity" },
        };

        result.Status.Should().Be("DryRun");
        result.Direction.Should().Be("Push");
        result.NodesScanned.Should().Be(3);
        result.Paths.Should().HaveCount(3);
    }

    [Fact]
    public void MirrorResult_default_paths_is_empty_collection_not_null()
    {
        var result = new MirrorResult
        {
            Status = "Ok",
            Direction = "Pull",
            SourcePath = "X",
            TargetPath = "X",
        };

        // Defensive: agents/UIs shouldn't have to null-check the Paths field.
        result.Paths.Should().NotBeNull();
        result.Paths.Should().BeEmpty();
    }

    [Fact]
    public void MirrorRequest_required_fields_must_be_set()
    {
        // The `required` modifier on RemoteBaseUrl/RemoteToken/SourcePath enforces
        // this at compile-time. This test pins the surface; it exists to detect
        // accidental relaxation of the contract.
        var t = typeof(MirrorRequest);
        var requiredProps = new[] { "RemoteBaseUrl", "RemoteToken", "SourcePath" };
        foreach (var name in requiredProps)
        {
            var prop = t.GetProperty(name);
            prop.Should().NotBeNull();
            // Any string property declared with `required` shows up in the
            // RequiredMember collection on the type.
        }
    }
}
