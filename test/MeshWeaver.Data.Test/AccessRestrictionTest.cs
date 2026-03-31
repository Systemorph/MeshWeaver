using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data.Validation;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

#region Test Data Models

/// <summary>
/// Entity with restricted creation - only admins can create
/// </summary>
public record RestrictedEntity(
    string Id,
    string Name,
    string Category
);

/// <summary>
/// Entity with owner-based restrictions - only owner can modify
/// </summary>
public record OwnedEntity(
    string Id,
    string Name,
    string OwnerId
);

/// <summary>
/// Simple entity for global restriction tests
/// </summary>
public record SimpleEntity(
    string Id,
    string Name
);

#endregion

/// <summary>
/// Tests for type-level access restrictions.
/// Demonstrates preventing Create operations on certain types based on user roles.
/// </summary>
public class TypeLevelAccessRestrictionTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected static RestrictedEntity[] InitialRestrictedData =>
    [
        new("1", "Existing Restricted", "general")
    ];

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(dataSource =>
                    dataSource.WithType<RestrictedEntity>(type =>
                        type.WithKey(instance => instance.Id)
                            .WithInitialData(_ => Task.FromResult(InitialRestrictedData.AsEnumerable()))
                    )
                )
            );
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(CreateHostAddress(), dataSource =>
                    dataSource.WithType<RestrictedEntity>(type =>
                        type
                            // Only Admin role can Create - validation runs on client
                            .WithAccessRestriction(
                                (action, ctx, accessCtx, ct) =>
                                {
                                    // Allow all actions except Create for non-admins
                                    if (action != AccessAction.Create)
                                        return Task.FromResult(true);
                                    // For Create, require Admin role
                                    return Task.FromResult(
                                        accessCtx.UserContext?.Roles?.Contains("Admin") == true);
                                },
                                "AdminOnlyCreate")
                    ))
            );

    [Fact]
    public async Task Create_AsNonAdmin_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set non-admin user context
        accessService.SetContext(new AccessContext
        {
            ObjectId = "user123",
            Name = "Regular User",
            Roles = ["User"]
        });

        var newItem = new RestrictedEntity("2", "New Restricted", "test");

        // Act
        var response = await client.AwaitResponse(
            new DataChangeRequest { Creations = [newItem] },
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
        dataResponse.Log.Messages.Should().Contain(m =>
            m.Message.Contains("Access denied") || m.Message.Contains("Unauthorized"));
    }

    [Fact]
    public async Task Create_AsAdmin_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set admin user context
        accessService.SetContext(new AccessContext
        {
            ObjectId = "admin123",
            Name = "Admin User",
            Roles = ["Admin"]
        });

        var newItem = new RestrictedEntity("3", "Admin Created", "test");

        // Act
        var response = await client.AwaitResponse(
            new DataChangeRequest { Creations = [newItem] },
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Committed);

        // Verify item was created
        var workspace = client.GetWorkspace();
        var items = await workspace
            .GetObservable<RestrictedEntity>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync(x => x.Any(item => item.Id == "3"));

        items.Should().Contain(x => x.Id == "3" && x.Name == "Admin Created");
    }

    [Fact]
    public async Task Read_AsNonAdmin_ShouldSucceed()
    {
        // Arrange - Read is allowed for all users
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set non-admin user context
        accessService.SetContext(new AccessContext
        {
            ObjectId = "user456",
            Name = "Regular User",
            Roles = ["User"]
        });

        // Act - Read existing items
        var workspace = client.GetWorkspace();
        var items = await workspace
            .GetObservable<RestrictedEntity>()
            .Timeout(5.Seconds())
            .FirstAsync();

        // Assert - Should be able to read
        items.Should().NotBeEmpty();
        items.Should().Contain(x => x.Id == "1");
    }
}

/// <summary>
/// Tests for row-level access restrictions.
/// Demonstrates preventing operations on specific entity instances based on ownership.
/// </summary>
public class RowLevelAccessRestrictionTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected static OwnedEntity[] InitialOwnedData =>
    [
        new("1", "User1's Entity", "user1"),
        new("2", "User2's Entity", "user2"),
        new("3", "Shared Entity", "shared")
    ];

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(dataSource =>
                    dataSource.WithType<OwnedEntity>(type =>
                        type.WithKey(instance => instance.Id)
                            .WithInitialData(_ => Task.FromResult(InitialOwnedData.AsEnumerable()))
                    )
                )
            );
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(CreateHostAddress(), dataSource =>
                    dataSource.WithType<OwnedEntity>(type =>
                        type
                            // Row-level restriction: only owner can Update or Delete
                            .WithTypedAccessRestriction(
                                (action, entity, accessCtx, ct) =>
                                {
                                    // Read is allowed for all
                                    if (action == AccessAction.Read)
                                        return true;

                                    // Create is allowed for all authenticated users
                                    if (action == AccessAction.Create)
                                        return accessCtx.UserContext != null;

                                    // Update and Delete require ownership
                                    var userId = accessCtx.UserContext?.ObjectId;
                                    return entity.OwnerId == userId || entity.OwnerId == "shared";
                                },
                                "OwnerOnly")
                    ))
            );

    [Fact]
    public async Task Update_OtherUsersEntity_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set user1's context
        accessService.SetContext(new AccessContext
        {
            ObjectId = "user1",
            Name = "User One"
        });

        // Try to update user2's entity
        var updatedItem = new OwnedEntity("2", "Hijacked Name", "user2");

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Update([updatedItem]),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
        dataResponse.Log.Messages.Should().Contain(m =>
            m.Message.Contains("Access denied") || m.Message.Contains("OwnerOnly"));
    }

    [Fact]
    public async Task Update_OwnEntity_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set user1's context
        accessService.SetContext(new AccessContext
        {
            ObjectId = "user1",
            Name = "User One"
        });

        // Update user1's own entity
        var updatedItem = new OwnedEntity("1", "Updated My Entity", "user1");

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Update([updatedItem]),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Committed);

        // Verify item was updated
        var workspace = client.GetWorkspace();
        var items = await workspace
            .GetObservable<OwnedEntity>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync(x => x.Any(item => item.Name == "Updated My Entity"));

        items.Should().Contain(x => x.Id == "1" && x.Name == "Updated My Entity");
    }

    [Fact]
    public async Task Delete_OtherUsersEntity_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set user1's context
        accessService.SetContext(new AccessContext
        {
            ObjectId = "user1",
            Name = "User One"
        });

        // Try to delete user2's entity
        var entityToDelete = new OwnedEntity("2", "User2's Entity", "user2");

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Delete([entityToDelete], "user1"),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);

        // Verify entity still exists
        var workspace = client.GetWorkspace();
        var items = await workspace
            .GetObservable<OwnedEntity>()
            .Timeout(5.Seconds())
            .FirstAsync();
        items.Should().Contain(x => x.Id == "2");
    }

    [Fact]
    public async Task Delete_SharedEntity_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set any user's context
        accessService.SetContext(new AccessContext
        {
            ObjectId = "anyuser",
            Name = "Any User"
        });

        // Delete shared entity (owned by "shared" - special case allowing all)
        var entityToDelete = new OwnedEntity("3", "Shared Entity", "shared");

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Delete([entityToDelete], "anyuser"),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Committed);
    }
}

/// <summary>
/// Tests for global access restrictions.
/// Demonstrates preventing anonymous operations across all types.
/// </summary>
public class GlobalAccessRestrictionTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected static SimpleEntity[] InitialSimpleData =>
    [
        new("1", "Simple Item 1"),
        new("2", "Simple Item 2")
    ];

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(dataSource =>
                    dataSource.WithType<SimpleEntity>(type =>
                        type.WithKey(instance => instance.Id)
                            .WithInitialData(_ => Task.FromResult(InitialSimpleData.AsEnumerable()))
                    )
                )
            );
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data
                    // Global restriction: no anonymous deletes - validation runs on client
                    // Note: The framework's PostPipeline always provides a non-null AccessContext
                    // (using the hub address as fallback), so we check for explicit user roles
                    // rather than null to distinguish authenticated vs anonymous users.
                    .WithAccessRestriction(
                        (action, ctx, accessCtx) =>
                        {
                            // Allow everything except anonymous deletes
                            if (action != AccessAction.Delete)
                                return true;
                            // For Delete, require authenticated user (must have roles)
                            return accessCtx.UserContext?.Roles?.Count > 0;
                        },
                        "NoAnonymousDeletes")
                    .AddHubSource(CreateHostAddress(), dataSource =>
                        dataSource.WithType<SimpleEntity>())
            );

    [Fact]
    public async Task Delete_AsAnonymous_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set no user context (anonymous - no roles)
        accessService.SetContext(null);

        var entityToDelete = new SimpleEntity("1", "Simple Item 1");

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Delete([entityToDelete], "anonymous"),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
        dataResponse.Log.Messages.Should().Contain(m =>
            m.Message.Contains("Access denied") || m.Message.Contains("NoAnonymousDeletes"));

        // Verify entity still exists
        var workspace = client.GetWorkspace();
        var items = await workspace
            .GetObservable<SimpleEntity>()
            .Timeout(5.Seconds())
            .FirstAsync();
        items.Should().Contain(x => x.Id == "1");
    }

    [Fact]
    public async Task Delete_AsAuthenticated_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set authenticated user context with roles
        accessService.SetContext(new AccessContext
        {
            ObjectId = "authuser",
            Name = "Authenticated User",
            Roles = ["User"]
        });

        var entityToDelete = new SimpleEntity("2", "Simple Item 2");

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Delete([entityToDelete], "authuser"),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Committed);
    }

    [Fact]
    public async Task Create_AsAnonymous_ShouldSucceed()
    {
        // Arrange - Create is allowed for anonymous (only Delete is restricted)
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set no user context (anonymous)
        accessService.SetContext(null);

        var newItem = new SimpleEntity("3", "Anonymous Created");

        // Act
        var response = await client.AwaitResponse(
            new DataChangeRequest { Creations = [newItem] },
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Committed);

        // Verify item was created
        var workspace = client.GetWorkspace();
        var items = await workspace
            .GetObservable<SimpleEntity>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync(x => x.Any(item => item.Id == "3"));

        items.Should().Contain(x => x.Id == "3" && x.Name == "Anonymous Created");
    }
}

/// <summary>
/// Tests for combined global and type-level restrictions.
/// Demonstrates how restrictions are evaluated in order (global first, then type-specific).
/// </summary>
public class CombinedAccessRestrictionTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected static RestrictedEntity[] InitialData =>
    [
        new("1", "Test Entity", "general")
    ];

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(dataSource =>
                    dataSource.WithType<RestrictedEntity>(type =>
                        type.WithKey(instance => instance.Id)
                            .WithInitialData(_ => Task.FromResult(InitialData.AsEnumerable()))
                    )
                )
            );
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data
                    // Global restriction: require authentication for all write operations
                    // Note: The framework's PostPipeline always provides a non-null AccessContext
                    // (using the hub address as fallback), so we check for explicit user roles
                    // rather than null to distinguish authenticated vs anonymous users.
                    .WithAccessRestriction(
                        (action, ctx, accessCtx) =>
                        {
                            if (action == AccessAction.Read)
                                return true;
                            return accessCtx.UserContext?.Roles?.Count > 0;
                        },
                        "RequireAuthentication")
                    .AddHubSource(CreateHostAddress(), dataSource =>
                        dataSource.WithType<RestrictedEntity>(type =>
                            type
                                // Type-specific: only Admin can Create
                                .WithAccessRestriction(
                                    (action, ctx, accessCtx, ct) =>
                                    {
                                        if (action != AccessAction.Create)
                                            return Task.FromResult(true);
                                        return Task.FromResult(
                                            accessCtx.UserContext?.Roles?.Contains("Admin") == true);
                                    },
                                    "AdminOnlyCreate")
                        ))
            );

    [Fact]
    public async Task Create_AsAnonymous_ShouldFailGlobalRestriction()
    {
        // Arrange
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set no user context (anonymous)
        accessService.SetContext(null);

        var newItem = new RestrictedEntity("2", "Anonymous Attempt", "test");

        // Act
        var response = await client.AwaitResponse(
            new DataChangeRequest { Creations = [newItem] },
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert - Should fail at global restriction (RequireAuthentication)
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
    }

    [Fact]
    public async Task Create_AsAuthenticatedNonAdmin_ShouldFailTypeRestriction()
    {
        // Arrange
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set authenticated but non-admin context
        accessService.SetContext(new AccessContext
        {
            ObjectId = "user123",
            Name = "Regular User",
            Roles = ["User"]
        });

        var newItem = new RestrictedEntity("3", "User Attempt", "test");

        // Act
        var response = await client.AwaitResponse(
            new DataChangeRequest { Creations = [newItem] },
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert - Should pass global but fail type-specific (AdminOnlyCreate)
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
    }

    [Fact]
    public async Task Create_AsAdmin_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set admin context
        accessService.SetContext(new AccessContext
        {
            ObjectId = "admin123",
            Name = "Admin User",
            Roles = ["Admin"]
        });

        var newItem = new RestrictedEntity("4", "Admin Created", "test");

        // Act
        var response = await client.AwaitResponse(
            new DataChangeRequest { Creations = [newItem] },
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert - Should pass both global and type-specific restrictions
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Committed);
    }

    [Fact]
    public async Task Update_AsAuthenticatedNonAdmin_ShouldSucceed()
    {
        // Arrange - Update only requires authentication (AdminOnlyCreate is for Create only)
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Set authenticated but non-admin context
        accessService.SetContext(new AccessContext
        {
            ObjectId = "user456",
            Name = "Regular User",
            Roles = ["User"]
        });

        var updatedItem = new RestrictedEntity("1", "Updated by User", "general");

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Update([updatedItem]),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert - Update should succeed (not restricted to Admin)
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Committed);
    }
}
