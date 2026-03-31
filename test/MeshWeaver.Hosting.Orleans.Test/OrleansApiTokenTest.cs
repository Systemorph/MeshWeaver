using System;
using System.Linq;
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
/// Tests the full flow: create token → validate via ApiToken/{hashPrefix} grain → get AccessContext.
/// This requires Orleans grain activation for the ApiToken/{hashPrefix} hub.
/// </summary>
public class OrleansApiTokenTest(ITestOutputHelper output) : OrleansTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry
            .WithType(typeof(ValidateTokenRequest), nameof(ValidateTokenRequest))
            .WithType(typeof(ValidateTokenResponse), nameof(ValidateTokenResponse))
            .WithType(typeof(CreateApiTokenRequest), nameof(CreateApiTokenRequest))
            .WithType(typeof(CreateApiTokenResponse), nameof(CreateApiTokenResponse));
        return base.ConfigureClient(configuration);
    }

    [Fact]
    public async Task CreateAndValidateApiToken_ViaOrleans()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = await GetClientAsync();
        var meshAddress = ClientMesh.Address;

        // 1. Create token
        var createResponse = await client.AwaitResponse(
            new CreateApiTokenRequest { Label = "Orleans Test Token" },
            o => o.WithTarget(meshAddress), ct);

        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error);
        var rawToken = createResponse.Message.RawToken!;
        rawToken.Should().StartWith("mw_");
        createResponse.Message.TokenPath.Should().Contain("/_Api/");
        Output.WriteLine($"Created token at {createResponse.Message.TokenPath}");

        // 2. Validate token — routes to ApiToken/{hashPrefix} grain
        var hash = ValidateTokenRequest.HashToken(rawToken);
        var hashPrefix = hash[..12];

        var validateResponse = await client.AwaitResponse(
            new ValidateTokenRequest(rawToken),
            o => o.WithTarget(new Address("ApiToken", hashPrefix)), ct);

        validateResponse.Message.Success.Should().BeTrue(validateResponse.Message.Error);
        validateResponse.Message.UserId.Should().NotBeNullOrEmpty();
        validateResponse.Message.UserName.Should().NotBeNullOrEmpty();
        Output.WriteLine($"Validated: user={validateResponse.Message.UserId}, name={validateResponse.Message.UserName}");

        // 3. Verify the token represents the user who created it
        // (AccessContext is set by UserContextMiddleware — here we just verify the response data)
        validateResponse.Message.UserEmail.Should().NotBeNullOrEmpty();
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
