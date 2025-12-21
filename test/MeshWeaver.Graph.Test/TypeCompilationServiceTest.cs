using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for TypeCompilationService - compiling C# type definitions at runtime using Roslyn.
/// These tests are fast since we use direct Roslyn CSharpCompilation.
/// </summary>
public class TypeCompilationServiceTest
{
    private readonly TypeCompilationService _service;
    private readonly ITypeRegistry _typeRegistry;

    public TypeCompilationServiceTest()
    {
        _typeRegistry = new TestTypeRegistry();
        _service = new TypeCompilationService(_typeRegistry);
    }

    [Fact(Timeout = 10000)]
    public async Task CompileTypeAsync_CompilesSimpleRecord()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "simple-record",
            DisplayName = "Simple",
            TypeSource = @"
public record SimpleRecord
{
    public string Name { get; init; } = string.Empty;
    public int Value { get; init; }
}"
        };

        // Act
        var compiledType = await _service.CompileTypeAsync(dataModel);

        // Assert
        compiledType.Should().NotBeNull();
        compiledType.Name.Should().Be("SimpleRecord");
        compiledType.GetProperty("Name").Should().NotBeNull();
        compiledType.GetProperty("Value").Should().NotBeNull();
    }

    [Fact(Timeout = 10000)]
    public async Task CompileTypeAsync_CompilesRecordWithKeyAttribute()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "record-with-key",
            DisplayName = "KeyedRecord",
            TypeSource = @"
public record KeyedRecord
{
    [Key]
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}"
        };

        // Act
        var compiledType = await _service.CompileTypeAsync(dataModel);

        // Assert
        compiledType.Should().NotBeNull();
        compiledType.Name.Should().Be("KeyedRecord");

        var idProperty = compiledType.GetProperty("Id");
        idProperty.Should().NotBeNull();
        idProperty!.GetCustomAttributes(typeof(KeyAttribute), false).Should().HaveCount(1);
    }

    [Fact(Timeout = 10000)]
    public async Task CompileTypeAsync_CompilesRecordWithEnum()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "record-with-enum",
            DisplayName = "EnumRecord",
            TypeSource = @"
public record TaskItem
{
    [Key]
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public TaskStatus Status { get; init; } = TaskStatus.Pending;
}

public enum TaskStatus
{
    Pending,
    InProgress,
    Completed,
    Cancelled
}"
        };

        // Act
        var compiledType = await _service.CompileTypeAsync(dataModel);

        // Assert
        compiledType.Should().NotBeNull();
        compiledType.Name.Should().Be("TaskItem");

        var statusProperty = compiledType.GetProperty("Status");
        statusProperty.Should().NotBeNull();
        statusProperty!.PropertyType.IsEnum.Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public async Task CompileTypeAsync_RegistersTypeInRegistry()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "registered-type",
            DisplayName = "Registered",
            TypeSource = @"
public record RegisteredType
{
    public string Data { get; init; } = string.Empty;
}"
        };

        // Act
        await _service.CompileTypeAsync(dataModel);

        // Assert
        var resolvedType = _typeRegistry.GetType("RegisteredType");
        resolvedType.Should().NotBeNull();
        resolvedType!.Name.Should().Be("RegisteredType");
    }

    [Fact(Timeout = 10000)]
    public async Task CompileTypeAsync_SetsCompiledTypeOnDataModel()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "with-compiled-type",
            DisplayName = "WithCompiled",
            TypeSource = @"
public record WithCompiledType
{
    public string Field { get; init; } = string.Empty;
}"
        };

        // Act
        await _service.CompileTypeAsync(dataModel);

        // Assert
        dataModel.CompiledType.Should().NotBeNull();
        dataModel.CompiledType!.Name.Should().Be("WithCompiledType");
    }

    [Fact(Timeout = 10000)]
    public async Task CompileTypeAsync_CachesCompiledType()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "cached-type",
            DisplayName = "Cached",
            TypeSource = @"
public record CachedType
{
    public string Value { get; init; } = string.Empty;
}"
        };

        // Act
        var firstCompile = await _service.CompileTypeAsync(dataModel);
        var secondCompile = await _service.CompileTypeAsync(dataModel);

        // Assert
        firstCompile.Should().BeSameAs(secondCompile, "Same type should be returned from cache");
    }

    [Fact(Timeout = 5000)]
    public void GetCompiledType_ReturnsNullForUnknownId()
    {
        // Act
        var result = _service.GetCompiledType("unknown-id");

        // Assert
        result.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public async Task GetCompiledType_ReturnsCompiledTypeAfterCompilation()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "get-compiled-test",
            DisplayName = "GetCompiled",
            TypeSource = @"
public record GetCompiledTest
{
    public int Number { get; init; }
}"
        };

        await _service.CompileTypeAsync(dataModel);

        // Act
        var result = _service.GetCompiledType("get-compiled-test");

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("GetCompiledTest");
    }

    [Fact(Timeout = 30000)]
    public async Task CompileAllAsync_CompilesMultipleDataModels()
    {
        // Arrange
        var models = new[]
        {
            new DataModel
            {
                Id = "model1",
                DisplayName = "Model1",
                TypeSource = "public record Model1Type { public string A { get; init; } = string.Empty; }"
            },
            new DataModel
            {
                Id = "model2",
                DisplayName = "Model2",
                TypeSource = "public record Model2Type { public string B { get; init; } = string.Empty; }"
            },
            new DataModel
            {
                Id = "model3",
                DisplayName = "Model3",
                TypeSource = "public record Model3Type { public string C { get; init; } = string.Empty; }"
            }
        };

        // Act
        var result = await _service.CompileAllAsync(models);

        // Assert
        result.Should().HaveCount(3);
        result["model1"].Name.Should().Be("Model1Type");
        result["model2"].Name.Should().Be("Model2Type");
        result["model3"].Name.Should().Be("Model3Type");
    }

    [Fact(Timeout = 10000)]
    public async Task CompileTypeAsync_CompiledTypeCanBeInstantiated()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "instantiable-type",
            DisplayName = "Instantiable",
            TypeSource = @"
public record InstantiableType
{
    public string Name { get; init; } = ""Default"";
    public int Count { get; init; } = 42;
}"
        };

        // Act
        var compiledType = await _service.CompileTypeAsync(dataModel);

        // Assert
        var instance = Activator.CreateInstance(compiledType);
        instance.Should().NotBeNull();

        var nameProperty = compiledType.GetProperty("Name");
        var countProperty = compiledType.GetProperty("Count");

        nameProperty!.GetValue(instance).Should().Be("Default");
        countProperty!.GetValue(instance).Should().Be(42);
    }

    [Fact(Timeout = 10000)]
    public async Task CompileTypeAsync_ThrowsForInvalidCode()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "invalid-code",
            DisplayName = "Invalid",
            TypeSource = @"
public record InvalidType
{
    public string MissingClosingBrace { get; init; }
    // Missing closing brace intentionally
"
        };

        // Act & Assert
        var act = () => _service.CompileTypeAsync(dataModel);
        await act.Should().ThrowAsync<TypeCompilationException>();
    }
}
