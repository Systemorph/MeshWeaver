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

/// <summary>
/// Test data model for validation testing
/// </summary>
public record ValidatableData(
    string Id,
    string Name,
    string Category,
    bool IsProtected = false
);

#region Validators

/// <summary>
/// A validator that rejects entities with "forbidden" in their name (for creations).
/// </summary>
public class ForbiddenNameValidator : IDataValidator
{
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Create];

    public Task<DataValidationResult> ValidateAsync(DataValidationContext context, CancellationToken ct = default)
    {
        if (context.Entity is ValidatableData data && data.Name?.Contains("forbidden", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Task.FromResult(DataValidationResult.Invalid(
                $"Entity name '{data.Name}' contains forbidden word",
                DataValidationRejectionReason.ValidationFailed));
        }
        return Task.FromResult(DataValidationResult.Valid());
    }
}

/// <summary>
/// A validator that prevents changing category to "locked" (for updates).
/// </summary>
public class LockedCategoryValidator : IDataValidator
{
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Update];

    public Task<DataValidationResult> ValidateAsync(DataValidationContext context, CancellationToken ct = default)
    {
        if (context.Entity is ValidatableData data && data.Category?.Equals("locked", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Task.FromResult(DataValidationResult.Invalid(
                "Cannot set category to 'locked'",
                DataValidationRejectionReason.ValidationFailed));
        }
        return Task.FromResult(DataValidationResult.Valid());
    }
}

/// <summary>
/// A validator that prevents deletion of protected entities (for deletions).
/// </summary>
public class ProtectedEntityValidator : IDataValidator
{
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Delete];

    public Task<DataValidationResult> ValidateAsync(DataValidationContext context, CancellationToken ct = default)
    {
        if (context.Entity is ValidatableData data && data.IsProtected)
        {
            return Task.FromResult(DataValidationResult.Invalid(
                $"Entity '{data.Id}' is protected and cannot be deleted",
                DataValidationRejectionReason.Unauthorized));
        }
        return Task.FromResult(DataValidationResult.Valid());
    }
}

/// <summary>
/// A read validator that blocks reads of entities with "secret" in ID.
/// </summary>
public class SecretCategoryReadValidator : IDataValidator
{
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Read];

    public Task<DataValidationResult> ValidateAsync(DataValidationContext context, CancellationToken ct = default)
    {
        if (context.Entity is EntityReference entityRef && entityRef.Id is string id && id.Contains("secret", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(DataValidationResult.Invalid(
                "Cannot read secret entities",
                DataValidationRejectionReason.Unauthorized));
        }
        return Task.FromResult(DataValidationResult.Valid());
    }
}

#endregion

/// <summary>
/// Base test class for data validation tests
/// </summary>
public abstract class DataValidationTestBase(ITestOutputHelper output) : HubTestBase(output)
{
    protected static ValidatableData[] InitialTestData =>
    [
        new("1", "First Item", "general"),
        new("2", "Second Item", "general"),
        new("3", "Protected Item", "protected", IsProtected: true)
    ];

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(dataSource =>
                    dataSource.WithType<ValidatableData>(type =>
                        type.WithKey(instance => instance.Id)
                            .WithInitialData(_ => Task.FromResult(InitialTestData.AsEnumerable()))
                    )
                )
            );
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(CreateHostAddress(), dataSource =>
                    dataSource.WithType<ValidatableData>())
            );
}

/// <summary>
/// Tests for create validators
/// </summary>
public class DataCreateValidatorTest(ITestOutputHelper output) : DataValidationTestBase(output)
{
    protected CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .WithServices(services =>
                services.AddSingleton<IDataValidator, ForbiddenNameValidator>());
    }

    [Fact]
    public async Task Create_WithForbiddenName_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var newItem = new ValidatableData("4", "This is forbidden", "test");

        // Act
        var response = await client.AwaitResponse(
            new DataChangeRequest { Creations = [newItem] },
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
        dataResponse.Log.Messages.Should().Contain(m => m.Message.Contains("forbidden"));
    }

    [Fact]
    public async Task Create_WithAllowedName_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var newItem = new ValidatableData("4", "Allowed Name", "test");

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
            .GetObservable<ValidatableData>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync(x => x.Any(item => item.Id == "4"));

        items.Should().Contain(x => x.Id == "4" && x.Name == "Allowed Name");
    }
}

/// <summary>
/// Tests for update validators
/// </summary>
public class DataUpdateValidatorTest(ITestOutputHelper output) : DataValidationTestBase(output)
{
    protected CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .WithServices(services =>
                services.AddSingleton<IDataValidator, LockedCategoryValidator>());
    }

    [Fact]
    public async Task Update_ToLockedCategory_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var updatedItem = new ValidatableData("1", "First Item", "locked");

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Update([updatedItem]),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
        dataResponse.Log.Messages.Should().Contain(m => m.Message.Contains("locked"));
    }

    [Fact]
    public async Task Update_ToAllowedCategory_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var updatedItem = new ValidatableData("1", "First Item Updated", "general");

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
            .GetObservable<ValidatableData>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync(x => x.Any(item => item.Name == "First Item Updated"));

        items.Should().Contain(x => x.Id == "1" && x.Name == "First Item Updated");
    }
}

/// <summary>
/// Tests for delete validators
/// </summary>
public class DataDeleteValidatorTest(ITestOutputHelper output) : DataValidationTestBase(output)
{
    protected CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .WithServices(services =>
                services.AddSingleton<IDataValidator, ProtectedEntityValidator>());
    }

    [Fact]
    public async Task Delete_ProtectedEntity_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Get the protected item
        var items = await workspace
            .GetObservable<ValidatableData>()
            .Timeout(5.Seconds())
            .FirstAsync();
        var protectedItem = items.First(x => x.IsProtected);

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Delete([protectedItem], "TestUser"),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
        dataResponse.Log.Messages.Should().Contain(m => m.Message.Contains("protected"));

        // Verify item still exists
        var itemsAfter = await workspace
            .GetObservable<ValidatableData>()
            .Timeout(5.Seconds())
            .FirstAsync();
        itemsAfter.Should().Contain(x => x.Id == "3" && x.IsProtected);
    }

    [Fact]
    public async Task Delete_UnprotectedEntity_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Get an unprotected item
        var items = await workspace
            .GetObservable<ValidatableData>()
            .Timeout(5.Seconds())
            .FirstAsync();
        var unprotectedItem = items.First(x => !x.IsProtected);

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Delete([unprotectedItem], "TestUser"),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Committed);

        // Verify item was deleted
        var itemsAfter = await workspace
            .GetObservable<ValidatableData>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync(x => !x.Any(item => item.Id == unprotectedItem.Id));
        itemsAfter.Should().NotContain(x => x.Id == unprotectedItem.Id);
    }
}

/// <summary>
/// Tests for read validators via GetDataRequest
/// </summary>
public class DataReadValidatorTest(ITestOutputHelper output) : DataValidationTestBase(output)
{
    protected CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        // Register on host since GetDataRequest is sent to host
        return base.ConfigureHost(configuration)
            .WithServices(services =>
                services.AddSingleton<IDataValidator, SecretCategoryReadValidator>());
    }

    [Fact]
    public async Task GetData_SecretEntity_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var entityRef = new EntityReference(nameof(ValidatableData), "secret-item");

        // Act
        var response = await client.AwaitResponse(
            new GetDataRequest(entityRef),
            o => o.WithTarget(CreateHostAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<GetDataResponse>().Which;
        dataResponse.Error.Should().Contain("secret");
        dataResponse.Data.Should().BeNull();
    }

    [Fact]
    public async Task GetData_RegularEntity_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var entityRef = new EntityReference(nameof(ValidatableData), "1");

        // Act
        var response = await client.AwaitResponse(
            new GetDataRequest(entityRef),
            o => o.WithTarget(CreateHostAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<GetDataResponse>().Which;
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();
    }
}

/// <summary>
/// Tests for combined validators
/// </summary>
public class DataCombinedValidatorTest(ITestOutputHelper output) : DataValidationTestBase(output)
{
    protected CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .WithServices(services =>
            {
                services.AddSingleton<IDataValidator, ForbiddenNameValidator>();
                services.AddSingleton<IDataValidator, LockedCategoryValidator>();
                services.AddSingleton<IDataValidator, ProtectedEntityValidator>();
                return services;
            });
    }

    [Fact]
    public async Task CombinedValidators_CreateForbiddenUpdateAllowed_ShouldFailCreate()
    {
        // Arrange
        var client = GetClient();
        var newItem = new ValidatableData("5", "forbidden item", "general");

        // Act
        var response = await client.AwaitResponse(
            new DataChangeRequest { Creations = [newItem] },
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
    }

    [Fact]
    public async Task CombinedValidators_CreateAllowedUpdateLocked_ShouldFailUpdate()
    {
        // Arrange
        var client = GetClient();
        var updatedItem = new ValidatableData("1", "First Item", "locked");

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Update([updatedItem]),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
    }

    [Fact]
    public async Task CombinedValidators_DeleteProtected_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var items = await workspace
            .GetObservable<ValidatableData>()
            .Timeout(5.Seconds())
            .FirstAsync();
        var protectedItem = items.First(x => x.IsProtected);

        // Act
        var response = await client.AwaitResponse(
            DataChangeRequest.Delete([protectedItem], "TestUser"),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
    }

    [Fact]
    public async Task CombinedValidators_AllOperationsValid_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();

        // Create with allowed name
        var newItem = new ValidatableData("6", "New Valid Item", "general");
        var createResponse = await client.AwaitResponse(
            new DataChangeRequest { Creations = [newItem] },
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert create succeeded
        var createDataResponse = createResponse.Message.Should().BeOfType<DataChangeResponse>().Which;
        createDataResponse.Status.Should().Be(DataChangeStatus.Committed);

        // Update with allowed category
        var updatedItem = new ValidatableData("6", "Updated Valid Item", "general");
        var updateResponse = await client.AwaitResponse(
            DataChangeRequest.Update([updatedItem]),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert update succeeded
        var updateDataResponse = updateResponse.Message.Should().BeOfType<DataChangeResponse>().Which;
        updateDataResponse.Status.Should().Be(DataChangeStatus.Committed);

        // Delete unprotected item
        var itemToDelete = new ValidatableData("6", "Updated Valid Item", "general");
        var deleteResponse = await client.AwaitResponse(
            DataChangeRequest.Delete([itemToDelete], "TestUser"),
            o => o.WithTarget(CreateClientAddress()),
            TestTimeout);

        // Assert delete succeeded
        var deleteDataResponse = deleteResponse.Message.Should().BeOfType<DataChangeResponse>().Which;
        deleteDataResponse.Status.Should().Be(DataChangeStatus.Committed);
    }
}
