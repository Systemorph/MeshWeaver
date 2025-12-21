using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for ConfigurationStorageService - storing and loading JSON configuration files.
/// </summary>
public class ConfigurationStorageServiceTest : IDisposable
{
    private readonly string _testDirectory;
    private readonly ConfigurationStorageService _service;
    private readonly ITypeRegistry _typeRegistry;
    private readonly MeshNode _testNode;

    public ConfigurationStorageServiceTest()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"MeshWeaver_ConfigTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _typeRegistry = new TestTypeRegistry();
        _service = new ConfigurationStorageService(_testDirectory, _typeRegistry);
        _testNode = new MeshNode("test");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    #region DataModel Tests

    [Fact(Timeout = 10000)]
    public async Task SaveAsync_DataModel_CreatesJsonFile()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "test-model",
            DisplayName = "Test Model",
            IconName = "TestIcon",
            Description = "A test data model",
            DisplayOrder = 10,
            TypeSource = "public record TestModel { public string Id { get; init; } = string.Empty; }"
        };

        // Act
        await _service.SaveAsync(dataModel);

        // Assert
        var filePath = Path.Combine(_testDirectory, "_config", "dataModels", "test-model.json");
        File.Exists(filePath).Should().BeTrue("JSON file should be created");
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("test-model");
        content.Should().Contain("Test Model");
    }

    [Fact(Timeout = 10000)]
    public async Task LoadAllAsync_LoadsSavedDataModel()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "load-test",
            DisplayName = "Load Test",
            IconName = "Icon",
            Description = "Description",
            DisplayOrder = 5,
            TypeSource = "public record LoadTest { }"
        };
        await _service.SaveAsync(dataModel);

        // Act
        var loaded = await _service.LoadAllAsync(_testNode).ToListAsync();

        // Assert
        loaded.Should().HaveCount(1);
        var model = loaded.First().Should().BeOfType<DataModel>().Subject;
        model.Id.Should().Be("load-test");
        model.DisplayName.Should().Be("Load Test");
        model.TypeSource.Should().Contain("LoadTest");
    }

    [Fact(Timeout = 10000)]
    public async Task LoadAllAsync_LoadsMultipleDataModels()
    {
        // Arrange
        for (int i = 1; i <= 3; i++)
        {
            await _service.SaveAsync(new DataModel
            {
                Id = $"model-{i}",
                DisplayName = $"Model {i}",
                DisplayOrder = i,
                TypeSource = $"public record Model{i} {{ }}"
            });
        }

        // Act
        var loaded = await _service.LoadAllAsync(_testNode).ToListAsync();
        var dataModels = loaded.OfType<DataModel>().ToList();

        // Assert
        dataModels.Should().HaveCount(3);
        dataModels.Should().Contain(m => m.Id == "model-1");
        dataModels.Should().Contain(m => m.Id == "model-2");
        dataModels.Should().Contain(m => m.Id == "model-3");
    }

    #endregion

    #region NodeTypeConfig Tests

    [Fact(Timeout = 10000)]
    public async Task SaveAsync_NodeType_CreatesJsonFile()
    {
        // Arrange
        var nodeType = new NodeTypeConfig
        {
            NodeType = "story",
            DataModelId = "story-model",
            HubFeatureId = "default",
            DisplayName = "Story",
            IconName = "Document"
        };

        // Act
        await _service.SaveAsync(nodeType);

        // Assert
        var filePath = Path.Combine(_testDirectory, "_config", "nodeTypes", "story.json");
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public async Task LoadAllAsync_LoadsSavedNodeType()
    {
        // Arrange
        var nodeType = new NodeTypeConfig
        {
            NodeType = "project",
            DataModelId = "project-model",
            DisplayName = "Project"
        };
        await _service.SaveAsync(nodeType);

        // Act
        var loaded = await _service.LoadAllAsync(_testNode).ToListAsync();
        var nodeTypes = loaded.OfType<NodeTypeConfig>().ToList();

        // Assert
        nodeTypes.Should().HaveCount(1);
        nodeTypes.First().NodeType.Should().Be("project");
        nodeTypes.First().DataModelId.Should().Be("project-model");
    }

    #endregion

    #region HubFeatureConfig Tests

    [Fact(Timeout = 10000)]
    public async Task SaveAsync_HubFeature_CreatesJsonFile()
    {
        // Arrange
        var hubFeature = new HubFeatureConfig
        {
            Id = "graph",
            EnableMeshNavigation = true,
            EnableDynamicNodeTypeAreas = true,
            ContentCollections = ["persons", "logos"]
        };

        // Act
        await _service.SaveAsync(hubFeature);

        // Assert
        var filePath = Path.Combine(_testDirectory, "_config", "hubFeatures", "graph.json");
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public async Task LoadAllAsync_LoadsSavedHubFeature()
    {
        // Arrange
        var hubFeature = new HubFeatureConfig
        {
            Id = "test-hub",
            EnableMeshNavigation = false,
            EnableDynamicNodeTypeAreas = true,
            ContentCollections = ["collection1"]
        };
        await _service.SaveAsync(hubFeature);

        // Act
        var loaded = await _service.LoadAllAsync(_testNode).ToListAsync();
        var hubFeatures = loaded.OfType<HubFeatureConfig>().ToList();

        // Assert
        hubFeatures.Should().HaveCount(1);
        var feature = hubFeatures.First();
        feature.Id.Should().Be("test-hub");
        feature.EnableMeshNavigation.Should().BeFalse();
        feature.ContentCollections.Should().Contain("collection1");
    }

    #endregion

    #region ContentCollectionConfig Tests

    [Fact(Timeout = 10000)]
    public async Task SaveAsync_ContentCollection_CreatesJsonFile()
    {
        // Arrange
        var collection = new ContentCollectionConfig
        {
            Id = "avatars",
            Name = "avatars",
            SourceType = "FileSystem",
            ConfigurationKey = "avatarsPath"
        };

        // Act
        await _service.SaveAsync(collection);

        // Assert
        var filePath = Path.Combine(_testDirectory, "_config", "contentCollections", "avatars.json");
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public async Task LoadAllAsync_LoadsSavedContentCollection()
    {
        // Arrange
        var collection = new ContentCollectionConfig
        {
            Id = "images",
            Name = "images",
            SourceType = "FileSystem",
            BasePath = "/data/images"
        };
        await _service.SaveAsync(collection);

        // Act
        var loaded = await _service.LoadAllAsync(_testNode).ToListAsync();
        var collections = loaded.OfType<ContentCollectionConfig>().ToList();

        // Assert
        collections.Should().HaveCount(1);
        collections.First().Id.Should().Be("images");
        collections.First().BasePath.Should().Be("/data/images");
    }

    #endregion

    #region Path Escaping Tests

    [Fact(Timeout = 10000)]
    public async Task SaveAsync_EscapesSlashesInId()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "type/with/slashes",
            DisplayName = "Slashed Type",
            TypeSource = "public record SlashedType { }"
        };

        // Act
        await _service.SaveAsync(dataModel);

        // Assert
        var escapedPath = Path.Combine(_testDirectory, "_config", "dataModels", "type__with__slashes.json");
        File.Exists(escapedPath).Should().BeTrue("Slashes should be escaped to double underscores");
    }

    [Fact(Timeout = 10000)]
    public async Task LoadAllAsync_UnescapesSlashesInId()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "parent/child",
            DisplayName = "Hierarchical Type",
            TypeSource = "public record HierarchicalType { }"
        };
        await _service.SaveAsync(dataModel);

        // Act
        var loaded = await _service.LoadAllAsync(_testNode).ToListAsync();
        var dataModels = loaded.OfType<DataModel>().ToList();

        // Assert
        dataModels.Should().HaveCount(1);
        dataModels.First().Id.Should().Be("parent/child");
    }

    #endregion

    #region Delete Tests

    [Fact(Timeout = 10000)]
    public async Task DeleteAsync_RemovesDataModelFile()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "to-delete",
            DisplayName = "Delete Me",
            TypeSource = "public record DeleteMe { }"
        };
        await _service.SaveAsync(dataModel);
        var filePath = Path.Combine(_testDirectory, "_config", "dataModels", "to-delete.json");
        File.Exists(filePath).Should().BeTrue();

        // Act
        await _service.DeleteAsync<DataModel>("to-delete");

        // Assert
        File.Exists(filePath).Should().BeFalse();
    }

    #endregion

    #region Empty Directory Tests

    [Fact(Timeout = 10000)]
    public async Task LoadAllAsync_ReturnsEmptyWhenNoFiles()
    {
        // Act
        var loaded = await _service.LoadAllAsync(_testNode).ToListAsync();

        // Assert
        loaded.Should().BeEmpty();
    }

    #endregion

    #region Mixed Configuration Tests

    [Fact(Timeout = 10000)]
    public async Task LoadAllAsync_LoadsAllConfigurationTypes()
    {
        // Arrange
        await _service.SaveAsync(new DataModel
        {
            Id = "test-model",
            DisplayName = "Test Model",
            TypeSource = "public record TestModel { }"
        });
        await _service.SaveAsync(new NodeTypeConfig
        {
            NodeType = "test-node",
            DataModelId = "test-model"
        });
        await _service.SaveAsync(new HubFeatureConfig
        {
            Id = "test-feature",
            EnableMeshNavigation = true
        });
        await _service.SaveAsync(new ContentCollectionConfig
        {
            Id = "test-collection",
            Name = "test-collection"
        });

        // Act
        var loaded = await _service.LoadAllAsync(_testNode).ToListAsync();

        // Assert
        loaded.Should().HaveCount(4);
        loaded.OfType<DataModel>().Should().HaveCount(1);
        loaded.OfType<NodeTypeConfig>().Should().HaveCount(1);
        loaded.OfType<HubFeatureConfig>().Should().HaveCount(1);
        loaded.OfType<ContentCollectionConfig>().Should().HaveCount(1);
    }

    #endregion
}

/// <summary>
/// Simple test implementation of ITypeRegistry.
/// </summary>
internal class TestTypeRegistry : ITypeRegistry
{
    private readonly Dictionary<string, TestTypeDefinition> _typeByName = new();
    private readonly Dictionary<Type, string> _nameByType = new();
    private readonly List<Func<Type, KeyFunction?>> _keyFunctionProviders = new();

    public IEnumerable<KeyValuePair<string, ITypeDefinition>> Types =>
        _typeByName.Select(x => new KeyValuePair<string, ITypeDefinition>(x.Key, x.Value));

    public ITypeRegistry WithType(Type type) => WithType(type, type.Name);

    public ITypeRegistry WithType(Type type, string typeName)
    {
        _typeByName[typeName] = new TestTypeDefinition(type, typeName);
        _nameByType[type] = typeName;
        return this;
    }

    public KeyFunction? GetKeyFunction(string collection) =>
        _typeByName.TryGetValue(collection, out var td) ? td.KeyFunction : null;

    public KeyFunction? GetKeyFunction(Type type)
    {
        if (_nameByType.TryGetValue(type, out var name) && _typeByName.TryGetValue(name, out var td))
            return td.KeyFunction;
        foreach (var provider in _keyFunctionProviders)
        {
            var keyFunc = provider(type);
            if (keyFunc != null)
                return keyFunc;
        }
        return null;
    }

    public ITypeDefinition WithKeyFunction(string collection, KeyFunction keyFunction)
    {
        if (_typeByName.TryGetValue(collection, out var td))
        {
            td.KeyFunction = keyFunction;
            return td;
        }
        throw new ArgumentException($"Type {collection} not found");
    }

    public bool TryGetType(string name, out ITypeDefinition? type)
    {
        if (_typeByName.TryGetValue(name, out var td))
        {
            type = td;
            return true;
        }
        type = null;
        return false;
    }

    public Type? GetType(string name) =>
        _typeByName.TryGetValue(name, out var td) ? td.Type : null;

    public bool TryGetCollectionName(Type type, out string? typeName)
    {
        if (_nameByType.TryGetValue(type, out typeName))
            return true;
        typeName = null;
        return false;
    }

    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter)
    {
        foreach (var t in type.Assembly.GetTypes().Where(filter))
            WithType(t);
        return this;
    }

    public ITypeRegistry WithTypes(params IEnumerable<Type> types)
    {
        foreach (var t in types)
            WithType(t);
        return this;
    }

    public ITypeRegistry WithTypes(params IEnumerable<KeyValuePair<string, Type>> types)
    {
        foreach (var kvp in types)
            WithType(kvp.Value, kvp.Key);
        return this;
    }

    public string GetOrAddType(Type type, string? defaultName = null)
    {
        if (_nameByType.TryGetValue(type, out var name))
            return name;
        name = defaultName ?? type.Name;
        WithType(type, name);
        return name;
    }

    public ITypeRegistry WithKeyFunctionProvider(Func<Type, KeyFunction?> key)
    {
        _keyFunctionProviders.Add(key);
        return this;
    }

    public ITypeDefinition? GetTypeDefinition(Type type, bool create = true, string? typeName = null)
    {
        if (_nameByType.TryGetValue(type, out var name) && _typeByName.TryGetValue(name, out var td))
            return td;
        if (create)
        {
            typeName ??= type.Name;
            var newTd = new TestTypeDefinition(type, typeName);
            _typeByName[typeName] = newTd;
            _nameByType[type] = typeName;
            return newTd;
        }
        return null;
    }

    public ITypeDefinition? GetTypeDefinition(string collection) =>
        _typeByName.TryGetValue(collection, out var td) ? td : null;

    private class TestTypeDefinition(Type type, string collectionName) : ITypeDefinition
    {
        public Type Type { get; } = type;
        public string CollectionName { get; } = collectionName;
        public string DisplayName => CollectionName;
        public object? Icon => null;
        public int? Order => null;
        public string? GroupName => null;
        public string Description => string.Empty;
        public KeyFunction? KeyFunction { get; set; }
        public KeyFunction? Key => KeyFunction;

        public object GetKey(object instance)
        {
            if (KeyFunction != null)
                return KeyFunction.Function(instance);
            return instance.GetHashCode();
        }

        public Type GetKeyType()
        {
            return KeyFunction?.KeyType ?? typeof(int);
        }
    }
}
