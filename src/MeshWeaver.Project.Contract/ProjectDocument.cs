using System.Collections.Immutable;
using Systemorph.Notebook;

namespace MeshWeaver.Project.Contract;

public record ProjectIndexDocument(string Id, string Name, string CreatedBy, DateTime CreatedOn) 
{
    public const string Public = nameof(Public);

    public string HomeRegion { get; init; }
    public IReadOnlyCollection<string> Access { get; init; } = ImmutableList<string>.Empty;
    public string Abstract { get; init; }
    public string Thumbnail { get; init; }
    public IReadOnlyCollection<string> Tags { get; init; } = ImmutableList<string>.Empty;

    public long Views => ActivityByType.TryGetValue(UserActivityTypes.OpenProject, out var ret) ? ret : 0;
    public long Clones => ActivityByType.TryGetValue(UserActivityTypes.CloneProject, out var ret) ? ret : 0;
    public IReadOnlyDictionary<string, ProjectActivity> ActivityByUser { get; init; }
    public IDictionary<string, long> ActivityByType { get; init; } = ImmutableDictionary<string, long>.Empty;

    public static ProjectIndexDocument From(ProjectDocument project)
    {
        if (project == null)
            return null;

        return new ProjectIndexDocument(project.Id, project.Name, project.CreatedBy, project.CreatedOn)
               {
                   HomeRegion = project.HomeRegion,
                   Abstract = project.Abstract,
                   Thumbnail = project.Thumbnail,
                   Tags = project.Tags
               };
    }
    public static ProjectIndexDocument From(ProjectDto project)
    {
        if (project == null)
            return null;

        return new ProjectIndexDocument(project.Id, project.Name, project.CreatedBy, project.CreatedOn)
               {
                   HomeRegion = project.HomeRegion,
                   Abstract = project.Abstract,
                   Thumbnail = project.Thumbnail,
                   Tags = project.Tags,
               };
    }
}

public record ProjectActivity(DateTime Last);

public record ProjectGroup(string Id, string DisplayName, string Description, bool IsSystemGroup = false);

public record ProjectDocument(string Id, string Name, DateTime CreatedOn, string CreatedBy, DateTime LastModified)
{
    public string StorageName { get; set; }
    public Guid StreamId { get; init; }
    // we  ==> we.systemorph.cloud
    public string HomeRegion { get; init; }

    public int Version { get; init; }
    public IReadOnlyCollection<string> Environments { get; init; } = ImmutableHashSet<string>.Empty;
    public string DefaultEnvironment { get; init; }
    public bool ShouldSaveUserSettings { get; init; }

    public string Abstract { get; init; }
    public string Thumbnail { get; init; }
    public IReadOnlyCollection<string> Tags { get; init; } = ImmutableList<string>.Empty;
    public IReadOnlyCollection<Person> Authors { get; init; } = ImmutableList<Person>.Empty;
    public bool IsDeleted { get; init; }
    public int? Ttl { get; init; }
}

public record Person(string Id, string Name, string Affiliation)
{
    // TODO V10: How do we track last activities, likes, comments, etc (2022/01/07, Roland Buergi)
}