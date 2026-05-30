using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests for ApiToken creation via standard CreateNodeRequest.
/// ApiToken is a satellite of User — stored at User/{userId}/_Api/{hashPrefix}
/// with MainNode pointing to User/{userId}.
/// Access is controlled by Permission.Api (mapped in GetPermissionForNodeType).
/// Same pattern as Thread (Permission.Thread) and Comment (Permission.Comment).
/// </summary>
public class ApiTokenAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddSampleUsers();

    [Fact]
    public void CreateApiToken_ViaCreateNodeRequest_Succeeds()
    {
        // Generate token hash
        var rawToken = $"mw_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var hash = ValidateTokenRequest.HashToken(rawToken);
        var hashPrefix = hash[..12];

        // Create token as satellite of User node — same pattern as Thread
        var userId = "rbuergi";
        var tokenNode = new MeshNode(hashPrefix, $"User/{userId}/_Api")
        {
            Name = "Test API Token",
            NodeType = ApiTokenNodeType.NodeType,
            MainNode = $"User/{userId}",
            Content = new ApiToken
            {
                UserId = userId,
                UserName = "Roland",
                UserEmail = "rbuergi@systemorph.com",
                TokenHash = hash,
                Label = "Test Token",
                CreatedAt = DateTimeOffset.UtcNow,
            }
        };

        // Act — standard CreateNodeRequest
        var created = NodeFactory.CreateNode(tokenNode).Should().Emit();

        // Assert
        created.Should().NotBeNull();
        created.Path.Should().Be($"User/{userId}/_Api/{hashPrefix}");
        created.NodeType.Should().Be(ApiTokenNodeType.NodeType);
        created.MainNode.Should().Be($"User/{userId}");
        Output.WriteLine($"Token created at {created.Path}");
    }

    [Fact]
    public void CreateApiToken_StoredUnderUserPath()
    {
        var hash = ValidateTokenRequest.HashToken("mw_test_token_12345");
        var hashPrefix = hash[..12];
        var userId = "rbuergi";

        var tokenNode = new MeshNode(hashPrefix, $"User/{userId}/_Api")
        {
            Name = "Path Test Token",
            NodeType = ApiTokenNodeType.NodeType,
            MainNode = $"User/{userId}",
            Content = new ApiToken
            {
                UserId = userId,
                UserName = "Roland",
                UserEmail = "rbuergi@systemorph.com",
                TokenHash = hash,
                Label = "Path Test",
                CreatedAt = DateTimeOffset.UtcNow,
            }
        };

        NodeFactory.CreateNode(tokenNode).Should().Emit();

        // Verify via per-node stream (CQRS-correct — no catalog/index lag)
        var result = ReadNode($"User/{userId}/_Api/{hashPrefix}").Should().Emit();
        result.Should().NotBeNull("token should be retrievable by path");
        result!.MainNode.Should().Be($"User/{userId}");
    }
}
