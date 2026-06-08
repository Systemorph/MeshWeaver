using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Validation;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Threading.Tasks;
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

    public IObservable<DataValidationResult> Validate(DataValidationContext context)
    {
        if (context.Entity is ValidatableData data && data.Name?.Contains("forbidden", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Observable.Return(DataValidationResult.Invalid(
                $"Entity name '{data.Name}' contains forbidden word",
                DataValidationRejectionReason.ValidationFailed));
        }
        return Observable.Return(DataValidationResult.Valid());
    }
}

/// <summary>
/// A validator that prevents changing category to "locked" (for updates).
/// </summary>
public class LockedCategoryValidator : IDataValidator
{
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Update];

    public IObservable<DataValidationResult> Validate(DataValidationContext context)
    {
        if (context.Entity is ValidatableData data && data.Category?.Equals("locked", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Observable.Return(DataValidationResult.Invalid(
                "Cannot set category to 'locked'",
                DataValidationRejectionReason.ValidationFailed));
        }
        return Observable.Return(DataValidationResult.Valid());
    }
}

/// <summary>
/// A validator that prevents deletion of protected entities (for deletions).
/// </summary>
public class ProtectedEntityValidator : IDataValidator
{
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Delete];

    public IObservable<DataValidationResult> Validate(DataValidationContext context)
    {
        if (context.Entity is ValidatableData data && data.IsProtected)
        {
            return Observable.Return(DataValidationResult.Invalid(
                $"Entity '{data.Id}' is protected and cannot be deleted",
                DataValidationRejectionReason.Unauthorized));
        }
        return Observable.Return(DataValidationResult.Valid());
    }
}

/// <summary>
/// A read validator that blocks reads of entities with "secret" in ID.
/// </summary>
public class SecretCategoryReadValidator : IDataValidator
{
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Read];

    public IObservable<DataValidationResult> Validate(DataValidationContext context)
    {
        if (context.Entity is EntityReference entityRef && entityRef.Id is string id && id.Contains("secret", StringComparison.OrdinalIgnoreCase))
        {
            return Observable.Return(DataValidationResult.Invalid(
                "Cannot read secret entities",
                DataValidationRejectionReason.Unauthorized));
        }
        return Observable.Return(DataValidationResult.Valid());
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
                            .WithInitialData(_ => Observable.Return(InitialTestData.AsEnumerable()))
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
    public void Create_WithForbiddenName_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var newItem = new ValidatableData("4", "This is forbidden", "test");

        // Act
        var response = client.Observe(new DataChangeRequest { Creations = [newItem] }, o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
        dataResponse.Log.Messages.Should().Contain(m => m.Message.Contains("forbidden"));
    }

    [Fact]
    public void Create_WithAllowedName_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var newItem = new ValidatableData("4", "Allowed Name", "test");

        // Act
        var response = client.Observe(new DataChangeRequest { Creations = [newItem] }, o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Committed);

        // Verify item was created
        var workspace = client.GetWorkspace();
        var items = workspace
            .GetObservable<ValidatableData>()
            .Should().Within(5.Seconds())
            .Match(x => x.Any(item => item.Id == "4"));

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
    public void Update_ToLockedCategory_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var updatedItem = new ValidatableData("1", "First Item", "locked");

        // Act
        var response = client.Observe(DataChangeRequest.Update([updatedItem]), o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
        dataResponse.Log.Messages.Should().Contain(m => m.Message.Contains("locked"));
    }

    [Fact]
    public void Update_ToAllowedCategory_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var updatedItem = new ValidatableData("1", "First Item Updated", "general");

        // Act
        var response = client.Observe(DataChangeRequest.Update([updatedItem]), o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Committed);

        // Verify item was updated
        var workspace = client.GetWorkspace();
        var items = workspace
            .GetObservable<ValidatableData>()
            .Should().Within(5.Seconds())
            .Match(x => x.Any(item => item.Name == "First Item Updated"));

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
    public void Delete_ProtectedEntity_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Get the protected item
        var items = workspace
            .GetObservable<ValidatableData>()
            .Should().Within(5.Seconds())
            .Emit();
        var protectedItem = items.First(x => x.IsProtected);

        // Act
        var response = client.Observe(DataChangeRequest.Delete([protectedItem], "TestUser"), o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
        dataResponse.Log.Messages.Should().Contain(m => m.Message.Contains("protected"));

        // Verify item still exists
        var itemsAfter = workspace
            .GetObservable<ValidatableData>()
            .Should().Within(5.Seconds())
            .Emit();
        itemsAfter.Should().Contain(x => x.Id == "3" && x.IsProtected);
    }

    [Fact]
    public void Delete_UnprotectedEntity_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Get an unprotected item
        var items = workspace
            .GetObservable<ValidatableData>()
            .Should().Within(5.Seconds())
            .Emit();
        var unprotectedItem = items.First(x => !x.IsProtected);

        // Act
        var response = client.Observe(DataChangeRequest.Delete([unprotectedItem], "TestUser"), o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Committed);

        // Verify item was deleted
        var itemsAfter = workspace
            .GetObservable<ValidatableData>()
            .Should().Within(5.Seconds())
            .Match(x => !x.Any(item => item.Id == unprotectedItem.Id));
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
    public void GetData_SecretEntity_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var entityRef = new EntityReference(nameof(ValidatableData), "secret-item");

        // Act
        var response = client.Observe(new GetDataRequest(entityRef), o => o.WithTarget(CreateHostAddress())).Should().Within(10.Seconds()).Emit();

        // Assert
        var dataResponse = response.Message.Should().BeOfType<GetDataResponse>().Which;
        dataResponse.Error.Should().Contain("secret");
        dataResponse.Data.Should().BeNull();
    }

    [Fact]
    public void GetData_RegularEntity_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var entityRef = new EntityReference(nameof(ValidatableData), "1");

        // Act
        var response = client.Observe(new GetDataRequest(entityRef), o => o.WithTarget(CreateHostAddress())).Should().Within(10.Seconds()).Emit();

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
    public void CombinedValidators_CreateForbiddenUpdateAllowed_ShouldFailCreate()
    {
        // Arrange
        var client = GetClient();
        var newItem = new ValidatableData("5", "forbidden item", "general");

        // Act
        var response = client.Observe(new DataChangeRequest { Creations = [newItem] }, o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
    }

    [Fact]
    public void CombinedValidators_CreateAllowedUpdateLocked_ShouldFailUpdate()
    {
        // Arrange
        var client = GetClient();
        var updatedItem = new ValidatableData("1", "First Item", "locked");

        // Act
        var response = client.Observe(DataChangeRequest.Update([updatedItem]), o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
    }

    [Fact]
    public void CombinedValidators_DeleteProtected_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var items = workspace
            .GetObservable<ValidatableData>()
            .Should().Within(5.Seconds())
            .Emit();
        var protectedItem = items.First(x => x.IsProtected);

        // Act
        var response = client.Observe(DataChangeRequest.Delete([protectedItem], "TestUser"), o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert
        var dataResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataResponse.Status.Should().Be(DataChangeStatus.Failed);
    }

    [Fact]
    public void CombinedValidators_AllOperationsValid_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();

        // Create with allowed name
        var newItem = new ValidatableData("6", "New Valid Item", "general");
        var createResponse = client.Observe(new DataChangeRequest { Creations = [newItem] }, o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert create succeeded
        var createDataResponse = createResponse.Message.Should().BeOfType<DataChangeResponse>().Which;
        createDataResponse.Status.Should().Be(DataChangeStatus.Committed);

        // Update with allowed category
        var updatedItem = new ValidatableData("6", "Updated Valid Item", "general");
        var updateResponse = client.Observe(DataChangeRequest.Update([updatedItem]), o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert update succeeded
        var updateDataResponse = updateResponse.Message.Should().BeOfType<DataChangeResponse>().Which;
        updateDataResponse.Status.Should().Be(DataChangeStatus.Committed);

        // Delete unprotected item
        var itemToDelete = new ValidatableData("6", "Updated Valid Item", "general");
        var deleteResponse = client.Observe(DataChangeRequest.Delete([itemToDelete], "TestUser"), o => o.WithTarget(CreateClientAddress())).Should().Within(10.Seconds()).Emit();

        // Assert delete succeeded
        var deleteDataResponse = deleteResponse.Message.Should().BeOfType<DataChangeResponse>().Which;
        deleteDataResponse.Status.Should().Be(DataChangeStatus.Committed);
    }
}
