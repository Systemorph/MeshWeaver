namespace MeshWeaver.Project.Contract;

public record ProjectCatalogDto(string Id, string Name)
{
    public string HomeRegion { get; init; }

    public IReadOnlyCollection<string> Tags { get; init; }
    public IReadOnlyCollection<Person> Authors { get; init; }
    public string Abstract { get; init; }
    public string Thumbnail { get; init; }
    public DateTime CreatedOn { get; init; } = DateTime.UtcNow;
    public bool IsPublic { get; init; }

    public static ProjectCatalogDto From(ProjectIndexDocument projectDocument)
    {
        var ret = new ProjectCatalogDto(projectDocument.Id, projectDocument.Name)
                  {
                      Abstract = projectDocument.Abstract,
                      CreatedOn = projectDocument.CreatedOn,
                      HomeRegion = projectDocument.HomeRegion,
                      Tags = projectDocument.Tags,
                      Thumbnail = projectDocument.Thumbnail,
                      IsPublic = projectDocument.Access?.Contains(ProjectIndexDocument.Public) ?? false,
                  };
        return ret;
    }
}

public record ProjectDto(string Id, string Name) : ProjectCatalogDto(Id, Name)
{
    public IReadOnlyCollection<string> Environments { get; init; }
    public string DefaultEnvironment { get; init; }
    public bool ShouldSaveUserSettings { get; init; }
    public string CreatedBy { get; init; }
    public int Version { get; init; }
}

public enum AccessObjectType { User, Group }

public abstract record AccessObjectDto(string Name, AccessObjectType Type, bool Inherited = false);

public record AccessUserDto(string Name, bool Inherited = false) : AccessObjectDto(Name, AccessObjectType.User, Inherited)
{
    public string DisplayName { get; set; }
    public string AvatarUri { get; set; }
}

public record AccessGroupDto(string Name, bool Inherited = false) : AccessObjectDto(Name, AccessObjectType.Group, Inherited)
{
    public AccessGroupDto(ProjectGroup group, bool inherited = false)
        : this(group.Id, inherited)
    {
        Description = group.Description;
        DisplayName = group.DisplayName;
    }

    public string DisplayName { get; set; }
    public string Description { get; set; }
}

public record ProjectNodeDto(string Id, string Name)
{
    public string Path { get; init; }
    public string Kind { get; init; }
    public int Version { get; init; }
}

public static class ProjectNodeKinds
{
    public const string Folder = nameof(Folder);
    public const string Blob = nameof(Blob);
    public const string Notebook = nameof(Notebook);
}