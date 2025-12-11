using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.Graph.Persistence;

/// <summary>
/// File system implementation of IGraphStorageProvider.
/// Loads and saves graph data as JSON files.
/// </summary>
public class FileSystemGraphStorageProvider : IGraphStorageProvider
{
    private readonly string _dataDirectory;
    private readonly string _organizationsFile;
    private readonly string _verticesFile;
    private readonly string _commentsFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileSystemGraphStorageProvider(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        _organizationsFile = Path.Combine(dataDirectory, "organizations.json");
        _verticesFile = Path.Combine(dataDirectory, "vertices.json");
        _commentsFile = Path.Combine(dataDirectory, "comments.json");
    }

    public async Task<IEnumerable<Organization>> LoadOrganizationsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_organizationsFile))
            return [];

        var json = await File.ReadAllTextAsync(_organizationsFile, ct);
        return JsonSerializer.Deserialize<List<OrganizationDto>>(json, JsonOptions)?
            .Select(dto => dto.ToOrganization())
            .ToList() ?? [];
    }

    public async Task SaveOrganizationsAsync(IEnumerable<Organization> organizations, CancellationToken ct = default)
    {
        EnsureDirectoryExists();
        var dtos = organizations.Select(OrganizationDto.FromOrganization).ToList();
        var json = JsonSerializer.Serialize(dtos, JsonOptions);
        await File.WriteAllTextAsync(_organizationsFile, json, ct);
    }

    public async Task<IEnumerable<Vertex>> LoadVerticesAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_verticesFile))
            return [];

        var json = await File.ReadAllTextAsync(_verticesFile, ct);
        return JsonSerializer.Deserialize<List<VertexDto>>(json, JsonOptions)?
            .Select(dto => dto.ToVertex())
            .ToList() ?? [];
    }

    public async Task SaveVerticesAsync(IEnumerable<Vertex> vertices, CancellationToken ct = default)
    {
        EnsureDirectoryExists();
        var dtos = vertices.Select(VertexDto.FromVertex).ToList();
        var json = JsonSerializer.Serialize(dtos, JsonOptions);
        await File.WriteAllTextAsync(_verticesFile, json, ct);
    }

    public async Task<IEnumerable<VertexComment>> LoadCommentsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_commentsFile))
            return [];

        var json = await File.ReadAllTextAsync(_commentsFile, ct);
        return JsonSerializer.Deserialize<List<VertexCommentDto>>(json, JsonOptions)?
            .Select(dto => dto.ToVertexComment())
            .ToList() ?? [];
    }

    public async Task SaveCommentsAsync(IEnumerable<VertexComment> comments, CancellationToken ct = default)
    {
        EnsureDirectoryExists();
        var dtos = comments.Select(VertexCommentDto.FromVertexComment).ToList();
        var json = JsonSerializer.Serialize(dtos, JsonOptions);
        await File.WriteAllTextAsync(_commentsFile, json, ct);
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_dataDirectory))
            Directory.CreateDirectory(_dataDirectory);
    }

    // DTOs for JSON serialization

    private record OrganizationDto(
        string Name,
        string DisplayName,
        string? Description,
        string? IconName,
        DateTimeOffset CreatedAt,
        List<GraphNamespaceDto> Namespaces
    )
    {
        public Organization ToOrganization() => new(Name, DisplayName)
        {
            Description = Description,
            IconName = IconName,
            CreatedAt = CreatedAt,
            Namespaces = Namespaces.Select(ns => ns.ToGraphNamespace()).ToImmutableList()
        };

        public static OrganizationDto FromOrganization(Organization org) => new(
            org.Name,
            org.DisplayName,
            org.Description,
            org.IconName,
            org.CreatedAt,
            org.Namespaces.Select(GraphNamespaceDto.FromGraphNamespace).ToList()
        );
    }

    private record GraphNamespaceDto(
        string Name,
        List<VertexTypeConfigDto> Types,
        string? Description,
        string? IconName,
        int DisplayOrder
    )
    {
        public GraphNamespace ToGraphNamespace() => new(
            Name,
            Types.Select(t => t.ToVertexTypeConfig()).ToImmutableList()
        )
        {
            Description = Description,
            IconName = IconName,
            DisplayOrder = DisplayOrder
        };

        public static GraphNamespaceDto FromGraphNamespace(GraphNamespace ns) => new(
            ns.Name,
            ns.Types.Select(VertexTypeConfigDto.FromVertexTypeConfig).ToList(),
            ns.Description,
            ns.IconName,
            ns.DisplayOrder
        );
    }

    private record VertexTypeConfigDto(
        string Name,
        string DisplayName,
        List<string> SatelliteTypes,
        string? Description,
        string? IconName
    )
    {
        public VertexTypeConfig ToVertexTypeConfig() => new(Name, DisplayName)
        {
            SatelliteTypes = SatelliteTypes.ToImmutableList(),
            Description = Description,
            IconName = IconName
        };

        public static VertexTypeConfigDto FromVertexTypeConfig(VertexTypeConfig tc) => new(
            tc.Name,
            tc.DisplayName,
            tc.SatelliteTypes.ToList(),
            tc.Description,
            tc.IconName
        );
    }

    private record VertexDto(
        Guid Id,
        string Organization,
        string Namespace,
        string Type,
        string Name,
        string? Text,
        DateTimeOffset CreatedAt,
        DateTimeOffset ModifiedAt,
        List<string> Dependencies
    )
    {
        public Vertex ToVertex() => new(Id, Organization, Namespace, Type, Name)
        {
            Text = Text,
            CreatedAt = CreatedAt,
            ModifiedAt = ModifiedAt,
            Dependencies = Dependencies.ToImmutableList()
        };

        public static VertexDto FromVertex(Vertex v) => new(
            v.Id,
            v.Organization,
            v.Namespace,
            v.Type,
            v.Name,
            v.Text,
            v.CreatedAt,
            v.ModifiedAt,
            v.Dependencies.ToList()
        );
    }

    private record VertexCommentDto(
        Guid Id,
        Guid VertexId,
        string Author,
        string Text,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ModifiedAt
    )
    {
        public VertexComment ToVertexComment() => new(Id, VertexId, Author, Text)
        {
            CreatedAt = CreatedAt,
            ModifiedAt = ModifiedAt
        };

        public static VertexCommentDto FromVertexComment(VertexComment c) => new(
            c.Id,
            c.VertexId,
            c.Author,
            c.Text,
            c.CreatedAt,
            c.ModifiedAt
        );
    }
}
