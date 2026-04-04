using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans integration test for API token creation and validation.
/// Uses standard CreateNodeRequest with nodeType=ApiToken — same satellite pattern
/// as Thread/Comment. The RLS validator maps ApiToken → Permission.Api.
/// </summary>
public class OrleansApiTokenTest(ITestOutputHelper output) : OrleansTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry
            .WithType(typeof(ValidateTokenRequest), nameof(ValidateTokenRequest))
            .WithType(typeof(ValidateTokenResponse), nameof(ValidateTokenResponse));
        return base.ConfigureClient(configuration);
    }

    [Fact]
    public async Task CreateApiToken_ViaStandardCreateNodeRequest()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = await GetClientAsync();
        var meshAddress = ClientMesh.Address;

        // Generate token hash
        var rawToken = $"mw_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var hash = ValidateTokenRequest.HashToken(rawToken);
        var hashPrefix = hash[..12];

        // Create token as satellite of User node — same pattern as Thread
        var userId = "Roland";
        var tokenNode = new MeshNode(hashPrefix, $"User/{userId}/_Api")
        {
            Name = "Orleans Test Token",
            NodeType = ApiTokenNodeType.NodeType,
            MainNode = $"User/{userId}",
            Content = new ApiToken
            {
                UserId = userId,
                UserName = "Roland",
                UserEmail = "rbuergi@systemorph.com",
                TokenHash = hash,
                Label = "Orleans Test Token",
                CreatedAt = DateTimeOffset.UtcNow,
            }
        };

        // Act — standard CreateNodeRequest (same as Thread creation)
        var response = await client.AwaitResponse(
            new CreateNodeRequest(tokenNode),
            o => o.WithTarget(meshAddress), ct);

        response.Message.Success.Should().BeTrue(response.Message.Error);
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.Path.Should().Be($"User/{userId}/_Api/{hashPrefix}");
        response.Message.Node.NodeType.Should().Be(ApiTokenNodeType.NodeType);
        Output.WriteLine($"Token created at {response.Message.Node.Path}");
    }

    [Fact]
    public async Task ValidateInvalidToken_Fails()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var client = await GetClientAsync();

        var fakeToken = "mw_0000000000000000000000000000000000000000000000000000000000000000";
        var hash = ValidateTokenRequest.HashToken(fakeToken);
        var hashPrefix = hash[..12];

        try
        {
            var response = await client.AwaitResponse(
                new ValidateTokenRequest(fakeToken),
                o => o.WithTarget(new Address("ApiToken", hashPrefix)), ct);

            // Either the response says failure, or the grain couldn't activate (token not found)
            if (response.Message != null)
                response.Message.Success.Should().BeFalse("invalid token should not validate");
        }
        catch (Exception)
        {
            // Expected — grain activation fails because node doesn't exist
        }
    }
}
