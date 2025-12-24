using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        var cacheOptions = Options.Create(new CompilationCacheOptions());
        var cacheService = new CompilationCacheService(cacheOptions, NullLogger<CompilationCacheService>.Instance);
        _service = new TypeCompilationService(_typeRegistry, cacheService, cacheOptions, NullLogger<TypeCompilationService>.Instance);
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

/// <summary>
/// Tests for TypeCompilationService caching functionality - CompileTypeWithCacheAsync.
/// </summary>
public class TypeCompilationServiceCacheTest : IDisposable
{
    private readonly string _testCacheDir;
    private readonly TypeCompilationService _service;
    private readonly ITypeRegistry _typeRegistry;
    private readonly CompilationCacheService _cacheService;

    public TypeCompilationServiceCacheTest()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), $"type-compile-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testCacheDir);

        _typeRegistry = new TestTypeRegistry();

        var cacheOptions = Microsoft.Extensions.Options.Options.Create(new CompilationCacheOptions
        {
            CacheDirectory = _testCacheDir,
            EnableCompilationCache = true,
            EnableSourceDebugging = true
        });

        _cacheService = new CompilationCacheService(cacheOptions, NullLogger<CompilationCacheService>.Instance);
        _service = new TypeCompilationService(_typeRegistry, _cacheService, cacheOptions, NullLogger<TypeCompilationService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testCacheDir))
        {
            try
            {
                Directory.Delete(_testCacheDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact(Timeout = 15000)]
    public async Task CompileTypeWithCacheAsync_CreatesDllFile()
    {
        // Arrange
        var node = new MeshWeaver.Mesh.MeshNode("test/cached")
        {
            Name = "Test Cached",
            NodeType = "cached-type",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "cached-type",
            DisplayName = "Cached Type",
            TypeSource = @"
public record CachedTestType
{
    public string Name { get; init; } = string.Empty;
}"
        };

        // Act
        var compiledType = await _service.CompileTypeWithCacheAsync(dataModel, node, null, null);

        // Assert
        compiledType.Should().NotBeNull();
        compiledType.Name.Should().Be("CachedTestType");

        var sanitizedName = _cacheService.SanitizeNodeName(node.Path);
        var dllPath = _cacheService.GetDllPath(sanitizedName);
        File.Exists(dllPath).Should().BeTrue("DLL should be created in cache directory");
    }

    [Fact(Timeout = 15000)]
    public async Task CompileTypeWithCacheAsync_CreatesPdbFile()
    {
        // Arrange
        var node = new MeshWeaver.Mesh.MeshNode("test/pdb")
        {
            Name = "Test PDB",
            NodeType = "pdb-type",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "pdb-type",
            DisplayName = "PDB Type",
            TypeSource = @"
public record PdbTestType
{
    public int Value { get; init; }
}"
        };

        // Act
        await _service.CompileTypeWithCacheAsync(dataModel, node, null, null);

        // Assert
        var sanitizedName = _cacheService.SanitizeNodeName(node.Path);
        var pdbPath = _cacheService.GetPdbPath(sanitizedName);
        File.Exists(pdbPath).Should().BeTrue("PDB should be created in cache directory");
    }

    [Fact(Timeout = 15000)]
    public async Task CompileTypeWithCacheAsync_CreatesSourceFile_WhenDebuggingEnabled()
    {
        // Arrange
        var node = new MeshWeaver.Mesh.MeshNode("test/source")
        {
            Name = "Test Source",
            NodeType = "source-type",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "source-type",
            DisplayName = "Source Type",
            TypeSource = @"
public record SourceTestType
{
    public string Data { get; init; } = string.Empty;
}"
        };

        // Act
        await _service.CompileTypeWithCacheAsync(dataModel, node, null, null);

        // Assert
        var sanitizedName = _cacheService.SanitizeNodeName(node.Path);
        var sourcePath = _cacheService.GetSourcePath(sanitizedName);
        File.Exists(sourcePath).Should().BeTrue("Source file should be created for debugging");

        var sourceContent = File.ReadAllText(sourcePath);
        sourceContent.Should().Contain("public record SourceTestType");
        sourceContent.Should().Contain("Auto-generated from MeshNode");
    }

    [Fact(Timeout = 15000)]
    public async Task CompileTypeWithCacheAsync_UsesCacheOnSecondCall()
    {
        // Arrange
        var node = new MeshWeaver.Mesh.MeshNode("test/cache-hit")
        {
            Name = "Cache Hit Test",
            NodeType = "cache-hit",
            LastModified = DateTimeOffset.UtcNow.AddMinutes(-5) // Node was modified in the past
        };

        var dataModel = new DataModel
        {
            Id = "cache-hit",
            DisplayName = "Cache Hit",
            TypeSource = @"
public record CacheHitType
{
    public string Field { get; init; } = string.Empty;
}"
        };

        // First compilation
        var firstType = await _service.CompileTypeWithCacheAsync(dataModel, node, null, null);

        // Get file modification time after first compile
        var sanitizedName = _cacheService.SanitizeNodeName(node.Path);
        var dllPath = _cacheService.GetDllPath(sanitizedName);
        var firstModTime = File.GetLastWriteTimeUtc(dllPath);

        // Small delay to ensure timestamp would be different if recompiled
        await Task.Delay(100);

        // Act - Second compilation should use cache
        var secondType = await _service.CompileTypeWithCacheAsync(dataModel, node, null, null);

        // Assert
        secondType.Should().NotBeNull();
        var secondModTime = File.GetLastWriteTimeUtc(dllPath);
        secondModTime.Should().Be(firstModTime, "DLL should not be recompiled (cache hit)");
    }

    [Fact(Timeout = 15000)]
    public void IsCacheValid_ReturnsFalse_WhenNodeIsNewerThanDll()
    {
        // This test verifies the cache invalidation logic without actual compilation
        // (actual recompilation can't be tested because Assembly.LoadFrom locks the DLL)

        // Arrange - Create fake cache files with old timestamp
        var nodeName = "stale_test";
        var dllPath = _cacheService.GetDllPath(nodeName);
        var pdbPath = _cacheService.GetPdbPath(nodeName);
        var sourcePath = _cacheService.GetSourcePath(nodeName);

        File.WriteAllText(dllPath, "dummy dll");
        File.WriteAllText(pdbPath, "dummy pdb");
        File.WriteAllText(sourcePath, "dummy source");

        // Set file time to past
        var pastTime = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(dllPath, pastTime);
        File.SetLastWriteTimeUtc(pdbPath, pastTime);
        File.SetLastWriteTimeUtc(sourcePath, pastTime);

        // Node was modified after the cache was created
        var nodeLastModified = DateTimeOffset.UtcNow;

        // Act
        var isValid = _cacheService.IsCacheValid(nodeName, nodeLastModified);

        // Assert
        isValid.Should().BeFalse("Cache should be invalid when node is newer than DLL");
    }

    [Fact(Timeout = 15000)]
    public void IsCacheValid_ReturnsTrue_WhenDllIsNewerThanNode()
    {
        // Arrange - Create cache files
        var nodeName = "fresh_cache_test";
        var dllPath = _cacheService.GetDllPath(nodeName);
        var pdbPath = _cacheService.GetPdbPath(nodeName);
        var sourcePath = _cacheService.GetSourcePath(nodeName);

        File.WriteAllText(dllPath, "dummy dll");
        File.WriteAllText(pdbPath, "dummy pdb");
        File.WriteAllText(sourcePath, "dummy source");

        // Node was modified before cache was created
        var nodeLastModified = DateTimeOffset.UtcNow.AddHours(-1);

        // Act
        var isValid = _cacheService.IsCacheValid(nodeName, nodeLastModified);

        // Assert
        isValid.Should().BeTrue("Cache should be valid when DLL is newer than node");
    }

    [Fact(Timeout = 15000)]
    public async Task CompileTypeWithCacheAsync_IncludesMeshNodeAttributeInSource()
    {
        // Arrange
        var node = new MeshWeaver.Mesh.MeshNode("org/project")
        {
            Name = "My Project",
            NodeType = "project",
            Description = "Test project",
            IconName = "Folder",
            DisplayOrder = 5,
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "project",
            DisplayName = "Project",
            TypeSource = @"
public record ProjectType
{
    public string Title { get; init; } = string.Empty;
}"
        };

        // Act
        await _service.CompileTypeWithCacheAsync(dataModel, node, null, null);

        // Assert
        var sanitizedName = _cacheService.SanitizeNodeName(node.Path);
        var sourcePath = _cacheService.GetSourcePath(sanitizedName);
        var sourceContent = File.ReadAllText(sourcePath);

        sourceContent.Should().Contain("MeshNodeAttribute");
        sourceContent.Should().Contain("Name = \"My Project\"");
        sourceContent.Should().Contain("NodeType = \"project\"");
        sourceContent.Should().Contain("Description = \"Test project\"");
        sourceContent.Should().Contain("IconName = \"Folder\"");
        sourceContent.Should().Contain("DisplayOrder = 5");
    }

    [Fact(Timeout = 15000)]
    public async Task CompileTypeWithCacheAsync_IncludesHubConfigurationInSource()
    {
        // Arrange
        var node = new MeshWeaver.Mesh.MeshNode("test/hub-config")
        {
            Name = "Hub Config Test",
            NodeType = "hub-config",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "hub-config",
            DisplayName = "Hub Config",
            TypeSource = @"
public record HubConfigType
{
    public string Data { get; init; } = string.Empty;
}"
        };

        // Act
        await _service.CompileTypeWithCacheAsync(dataModel, node, null, null);

        // Assert
        var sanitizedName = _cacheService.SanitizeNodeName(node.Path);
        var sourcePath = _cacheService.GetSourcePath(sanitizedName);
        var sourceContent = File.ReadAllText(sourcePath);

        sourceContent.Should().Contain("ConfigureHub");
        sourceContent.Should().Contain("ConfigureMeshHub()");
        sourceContent.Should().Contain("WithDataType(typeof(MeshWeaver.Graph.Dynamic.HubConfigType))");
    }

    [Fact(Timeout = 15000)]
    public async Task CompileTypeWithCacheAsync_IncludesDynamicAreasWhenEnabled()
    {
        // Arrange
        var node = new MeshWeaver.Mesh.MeshNode("test/dynamic-areas")
        {
            Name = "Dynamic Areas Test",
            NodeType = "dynamic-areas",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "dynamic-areas",
            DisplayName = "Dynamic Areas",
            TypeSource = @"
public record DynamicAreasType
{
    public string Name { get; init; } = string.Empty;
}"
        };

        var hubFeatures = new HubFeatureConfig
        {
            Id = "features",
            EnableDynamicNodeTypeAreas = true
        };

        // Act
        await _service.CompileTypeWithCacheAsync(dataModel, node, null, hubFeatures);

        // Assert
        var sanitizedName = _cacheService.SanitizeNodeName(node.Path);
        var sourcePath = _cacheService.GetSourcePath(sanitizedName);
        var sourceContent = File.ReadAllText(sourcePath);

        sourceContent.Should().Contain("AddDynamicNodeTypeAreas()");
    }

    [Fact(Timeout = 15000)]
    public async Task CompileTypeWithCacheAsync_ExcludesDynamicAreasWhenDisabled()
    {
        // Arrange
        var node = new MeshWeaver.Mesh.MeshNode("test/no-dynamic-areas")
        {
            Name = "No Dynamic Areas Test",
            NodeType = "no-dynamic-areas",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "no-dynamic-areas",
            DisplayName = "No Dynamic Areas",
            TypeSource = @"
public record NoDynamicAreasType
{
    public string Name { get; init; } = string.Empty;
}"
        };

        var hubFeatures = new HubFeatureConfig
        {
            Id = "features",
            EnableDynamicNodeTypeAreas = false
        };

        // Act
        await _service.CompileTypeWithCacheAsync(dataModel, node, null, hubFeatures);

        // Assert
        var sanitizedName = _cacheService.SanitizeNodeName(node.Path);
        var sourcePath = _cacheService.GetSourcePath(sanitizedName);
        var sourceContent = File.ReadAllText(sourcePath);

        sourceContent.Should().Contain("// Dynamic areas disabled");
        sourceContent.Should().NotContain("AddDynamicNodeTypeAreas()");
    }
}

/// <summary>
/// Tests for TypeCompilationService when caching is disabled.
/// </summary>
public class TypeCompilationServiceCacheDisabledTest
{
    private readonly TypeCompilationService _service;
    private readonly ITypeRegistry _typeRegistry;

    public TypeCompilationServiceCacheDisabledTest()
    {
        _typeRegistry = new TestTypeRegistry();

        var cacheOptions = Options.Create(new CompilationCacheOptions
        {
            EnableCompilationCache = false
        });

        var cacheService = new CompilationCacheService(cacheOptions, NullLogger<CompilationCacheService>.Instance);
        _service = new TypeCompilationService(_typeRegistry, cacheService, cacheOptions, NullLogger<TypeCompilationService>.Instance);
    }

    [Fact(Timeout = 10000)]
    public async Task CompileTypeWithCacheAsync_FallsBackToInMemory_WhenCacheDisabled()
    {
        // Arrange
        var node = new MeshWeaver.Mesh.MeshNode("test/no-cache")
        {
            Name = "No Cache Test",
            NodeType = "no-cache",
            LastModified = DateTimeOffset.UtcNow
        };

        var dataModel = new DataModel
        {
            Id = "no-cache",
            DisplayName = "No Cache",
            TypeSource = @"
public record NoCacheType
{
    public string Value { get; init; } = string.Empty;
}"
        };

        // Act
        var compiledType = await _service.CompileTypeWithCacheAsync(dataModel, node, null, null);

        // Assert
        compiledType.Should().NotBeNull();
        compiledType.Name.Should().Be("NoCacheType");
    }
}
