using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.CreativeCloud.Domain.LayoutAreas;

/// <summary>
/// Layout areas for entity details views (Overview, Workflow, Dependencies).
/// Each entity type has its own hub, so URL pattern is: /{type}/{id}/{Area}
/// Example: /story/story-4/Overview
/// </summary>
public static class EntityDetailsLayoutAreas
{
    // Area name constants
    public const string Overview = nameof(Overview);
    public const string Workflow = nameof(Workflow);
    public const string Dependencies = nameof(Dependencies);

    #region Person Views

    [Display(GroupName = "Person", Name = "Overview", Order = 0)]
    public static IObservable<UiControl?> PersonOverview(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Person>()!
            .Select(persons => persons?.FirstOrDefault(p => p.Id == id))
            .Select(CreatePersonOverview)
            .StartWith(Controls.Markdown("*Loading person...*"));
    }

    private static UiControl? CreatePersonOverview(Person? person)
    {
        if (person == null)
            return Controls.Markdown("*Person not found*");

        var sb = new StringBuilder();
        sb.AppendLine($"# {person.FirstName} {person.LastName}");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(person.Company))
            sb.AppendLine($"**Company:** {person.Company}");
        if (!string.IsNullOrEmpty(person.Email))
            sb.AppendLine($"**Email:** {person.Email}");
        if (!string.IsNullOrEmpty(person.ContentArchetypeId))
            sb.AppendLine($"**Content Archetype:** {person.ContentArchetypeId}");

        return Controls.Markdown(sb.ToString());
    }

    [Display(GroupName = "Person", Name = "Dependencies", Order = 2)]
    public static IObservable<UiControl?> PersonDependencies(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Person>()!
            .Select(persons => persons?.FirstOrDefault(p => p.Id == id)?.Dependencies ?? Array.Empty<string>())
            .Select(deps => CreateDependenciesView(deps, host))
            .StartWith(Controls.Markdown("*Loading dependencies...*"));
    }

    #endregion

    #region StoryArch Views

    [Display(GroupName = "Arch", Name = "Overview", Order = 0)]
    public static IObservable<UiControl?> ArchOverview(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<StoryArch>()!
            .Select(arches => arches?.FirstOrDefault(a => a.Id == id))
            .Select(CreateArchOverview)
            .StartWith(Controls.Markdown("*Loading story arch...*"));
    }

    private static UiControl? CreateArchOverview(StoryArch? arch)
    {
        if (arch == null)
            return Controls.Markdown("*Story arch not found*");

        var sb = new StringBuilder();
        sb.AppendLine($"# {arch.Name}");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(arch.Theme))
            sb.AppendLine($"**Theme:** {arch.Theme}");
        if (!string.IsNullOrEmpty(arch.Description))
        {
            sb.AppendLine();
            sb.AppendLine("## Description");
            sb.AppendLine(arch.Description);
        }

        return Controls.Markdown(sb.ToString());
    }

    [Display(GroupName = "Arch", Name = "Dependencies", Order = 2)]
    public static IObservable<UiControl?> ArchDependencies(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<StoryArch>()!
            .Select(arches => arches?.FirstOrDefault(a => a.Id == id)?.Dependencies ?? Array.Empty<string>())
            .Select(deps => CreateDependenciesView(deps, host))
            .StartWith(Controls.Markdown("*Loading dependencies...*"));
    }

    #endregion

    #region Story Views

    [Display(GroupName = "Story", Name = "Overview", Order = 0)]
    public static IObservable<UiControl?> StoryOverview(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Story>()!
            .Select(stories => stories?.FirstOrDefault(s => s.Id == id))
            .Select(CreateStoryOverview)
            .StartWith(Controls.Markdown("*Loading story...*"));
    }

    private static UiControl? CreateStoryOverview(Story? story)
    {
        if (story == null)
            return Controls.Markdown("*Story not found*");

        var sb = new StringBuilder();
        sb.AppendLine($"# {story.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Status:** {GetStatusIcon(story.Status)} {story.Status}");
        if (!string.IsNullOrEmpty(story.StoryArchId))
            sb.AppendLine($"**Story Arch:** {story.StoryArchId}");
        if (!string.IsNullOrEmpty(story.AuthorId))
            sb.AppendLine($"**Author:** {story.AuthorId}");
        if (story.CreatedAt.HasValue)
            sb.AppendLine($"**Created:** {story.CreatedAt.Value:yyyy-MM-dd}");
        if (story.PublishedAt.HasValue)
            sb.AppendLine($"**Published:** {story.PublishedAt.Value:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(story.Content))
        {
            sb.AppendLine();
            sb.AppendLine("## Content");
            sb.AppendLine(story.Content);
        }

        return Controls.Markdown(sb.ToString());
    }

    [Display(GroupName = "Story", Name = "Workflow", Order = 1)]
    public static IObservable<UiControl?> StoryWorkflow(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Story>()!
            .Select(stories => stories?.FirstOrDefault(s => s.Id == id))
            .Select(story => CreateWorkflowView(story?.Status, story?.Title, "Story"))
            .StartWith(Controls.Markdown("*Loading workflow...*"));
    }

    [Display(GroupName = "Story", Name = "Dependencies", Order = 2)]
    public static IObservable<UiControl?> StoryDependencies(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Story>()!
            .Select(stories => stories?.FirstOrDefault(s => s.Id == id)?.Dependencies ?? Array.Empty<string>())
            .Select(deps => CreateDependenciesView(deps, host))
            .StartWith(Controls.Markdown("*Loading dependencies...*"));
    }

    #endregion

    #region Post Views

    [Display(GroupName = "Post", Name = "Overview", Order = 0)]
    public static IObservable<UiControl?> PostOverview(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Post>()!
            .Select(posts => posts?.FirstOrDefault(p => p.Id == id))
            .Select(CreatePostOverview)
            .StartWith(Controls.Markdown("*Loading post...*"));
    }

    private static UiControl? CreatePostOverview(Post? post)
    {
        if (post == null)
            return Controls.Markdown("*Post not found*");

        var sb = new StringBuilder();
        sb.AppendLine($"# {post.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Status:** {GetStatusIcon(post.Status)} {post.Status}");
        if (!string.IsNullOrEmpty(post.Platform))
            sb.AppendLine($"**Platform:** {post.Platform}");
        if (!string.IsNullOrEmpty(post.ContentPillar))
            sb.AppendLine($"**Content Pillar:** {post.ContentPillar}");
        if (!string.IsNullOrEmpty(post.StoryId))
            sb.AppendLine($"**Story:** {post.StoryId}");
        if (post.ScheduledAt.HasValue)
            sb.AppendLine($"**Scheduled:** {post.ScheduledAt.Value:yyyy-MM-dd HH:mm}");
        if (post.PublishedAt.HasValue)
            sb.AppendLine($"**Published:** {post.PublishedAt.Value:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrEmpty(post.Content))
        {
            sb.AppendLine();
            sb.AppendLine("## Content");
            sb.AppendLine(post.Content);
        }

        return Controls.Markdown(sb.ToString());
    }

    [Display(GroupName = "Post", Name = "Workflow", Order = 1)]
    public static IObservable<UiControl?> PostWorkflow(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Post>()!
            .Select(posts => posts?.FirstOrDefault(p => p.Id == id))
            .Select(post => CreateWorkflowView(post?.Status, post?.Title, "Post"))
            .StartWith(Controls.Markdown("*Loading workflow...*"));
    }

    [Display(GroupName = "Post", Name = "Dependencies", Order = 2)]
    public static IObservable<UiControl?> PostDependencies(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Post>()!
            .Select(posts => posts?.FirstOrDefault(p => p.Id == id)?.Dependencies ?? Array.Empty<string>())
            .Select(deps => CreateDependenciesView(deps, host))
            .StartWith(Controls.Markdown("*Loading dependencies...*"));
    }

    #endregion

    #region Video Views

    [Display(GroupName = "Video", Name = "Overview", Order = 0)]
    public static IObservable<UiControl?> VideoOverview(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Video>()!
            .Select(videos => videos?.FirstOrDefault(v => v.Id == id))
            .Select(CreateVideoOverview)
            .StartWith(Controls.Markdown("*Loading video...*"));
    }

    private static UiControl? CreateVideoOverview(Video? video)
    {
        if (video == null)
            return Controls.Markdown("*Video not found*");

        var sb = new StringBuilder();
        sb.AppendLine($"# {video.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Status:** {GetStatusIcon(video.Status)} {video.Status}");
        if (!string.IsNullOrEmpty(video.Platform))
            sb.AppendLine($"**Platform:** {video.Platform}");
        if (!string.IsNullOrEmpty(video.StoryId))
            sb.AppendLine($"**Story:** {video.StoryId}");
        if (video.DurationSeconds.HasValue)
        {
            var mins = video.DurationSeconds.Value / 60;
            var secs = video.DurationSeconds.Value % 60;
            sb.AppendLine($"**Duration:** {mins}:{secs:D2}");
        }
        if (!string.IsNullOrEmpty(video.VideoUrl))
            sb.AppendLine($"**URL:** {video.VideoUrl}");
        if (!string.IsNullOrEmpty(video.Description))
        {
            sb.AppendLine();
            sb.AppendLine("## Description");
            sb.AppendLine(video.Description);
        }
        if (!string.IsNullOrEmpty(video.Transcript))
        {
            sb.AppendLine();
            sb.AppendLine("## Transcript");
            sb.AppendLine(video.Transcript);
        }

        return Controls.Markdown(sb.ToString());
    }

    [Display(GroupName = "Video", Name = "Workflow", Order = 1)]
    public static IObservable<UiControl?> VideoWorkflow(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Video>()!
            .Select(videos => videos?.FirstOrDefault(v => v.Id == id))
            .Select(video => CreateWorkflowView(video?.Status, video?.Title, "Video"))
            .StartWith(Controls.Markdown("*Loading workflow...*"));
    }

    [Display(GroupName = "Video", Name = "Dependencies", Order = 2)]
    public static IObservable<UiControl?> VideoDependencies(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Video>()!
            .Select(videos => videos?.FirstOrDefault(v => v.Id == id)?.Dependencies ?? Array.Empty<string>())
            .Select(deps => CreateDependenciesView(deps, host))
            .StartWith(Controls.Markdown("*Loading dependencies...*"));
    }

    #endregion

    #region Event Views

    [Display(GroupName = "Event", Name = "Overview", Order = 0)]
    public static IObservable<UiControl?> EventOverview(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Event>()!
            .Select(events => events?.FirstOrDefault(e => e.Id == id))
            .Select(CreateEventOverview)
            .StartWith(Controls.Markdown("*Loading event...*"));
    }

    private static UiControl? CreateEventOverview(Event? evt)
    {
        if (evt == null)
            return Controls.Markdown("*Event not found*");

        var sb = new StringBuilder();
        sb.AppendLine($"# {evt.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Status:** {GetStatusIcon(evt.Status)} {evt.Status}");
        if (!string.IsNullOrEmpty(evt.EventType))
            sb.AppendLine($"**Type:** {evt.EventType}");
        if (!string.IsNullOrEmpty(evt.StoryId))
            sb.AppendLine($"**Story:** {evt.StoryId}");
        if (evt.StartDate.HasValue)
            sb.AppendLine($"**Start:** {evt.StartDate.Value:yyyy-MM-dd HH:mm}");
        if (evt.EndDate.HasValue)
            sb.AppendLine($"**End:** {evt.EndDate.Value:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrEmpty(evt.Location))
            sb.AppendLine($"**Location:** {evt.Location}");
        if (!string.IsNullOrEmpty(evt.VirtualUrl))
            sb.AppendLine($"**Virtual URL:** {evt.VirtualUrl}");
        if (!string.IsNullOrEmpty(evt.Description))
        {
            sb.AppendLine();
            sb.AppendLine("## Description");
            sb.AppendLine(evt.Description);
        }

        return Controls.Markdown(sb.ToString());
    }

    [Display(GroupName = "Event", Name = "Workflow", Order = 1)]
    public static IObservable<UiControl?> EventWorkflow(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Event>()!
            .Select(events => events?.FirstOrDefault(e => e.Id == id))
            .Select(evt => CreateWorkflowView(evt?.Status, evt?.Title, "Event"))
            .StartWith(Controls.Markdown("*Loading workflow...*"));
    }

    [Display(GroupName = "Event", Name = "Dependencies", Order = 2)]
    public static IObservable<UiControl?> EventDependencies(LayoutAreaHost host, RenderingContext context)
    {
        var id = host.Hub.Address.Id;
        return host.Workspace.GetStream<Event>()!
            .Select(events => events?.FirstOrDefault(e => e.Id == id)?.Dependencies ?? Array.Empty<string>())
            .Select(deps => CreateDependenciesView(deps, host))
            .StartWith(Controls.Markdown("*Loading dependencies...*"));
    }

    #endregion

    #region Shared Helpers

    private static UiControl? CreateWorkflowView(ContentStatus? status, string? title, string entityType)
    {
        if (status == null)
            return Controls.Markdown($"*{entityType} not found*");

        var sb = new StringBuilder();
        sb.AppendLine($"# Workflow: {title}");
        sb.AppendLine();
        sb.AppendLine("## Current Status");
        sb.AppendLine($"{GetStatusIcon(status.Value)} **{status.Value}**");
        sb.AppendLine();
        sb.AppendLine("## Available Actions");

        switch (status.Value)
        {
            case ContentStatus.Draft:
                sb.AppendLine("- Submit for Review");
                break;
            case ContentStatus.InReview:
                sb.AppendLine("- Approve");
                sb.AppendLine("- Request Changes (back to Draft)");
                break;
            case ContentStatus.Approved:
                sb.AppendLine("- Schedule for Publication");
                sb.AppendLine("- Publish Now");
                break;
            case ContentStatus.Scheduled:
                sb.AppendLine("- Publish Now");
                sb.AppendLine("- Reschedule");
                sb.AppendLine("- Cancel (back to Approved)");
                break;
            case ContentStatus.Published:
                sb.AppendLine("- Archive");
                break;
            case ContentStatus.Archived:
                sb.AppendLine("- Restore (back to Draft)");
                break;
        }

        sb.AppendLine();
        sb.AppendLine("## Comments");
        sb.AppendLine("*Comments functionality coming soon*");

        return Controls.Markdown(sb.ToString());
    }

    private static UiControl? CreateDependenciesView(IReadOnlyCollection<string> dependencies, LayoutAreaHost host)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Dependencies");
        sb.AppendLine();

        if (dependencies.Count == 0)
        {
            sb.AppendLine("*No dependencies*");
            return Controls.Markdown(sb.ToString());
        }

        foreach (var dep in dependencies)
        {
            if (DependencyHelper.TryParse(dep, out var type, out var id))
            {
                // Link to the entity's Overview page: /{type}/{id}/Overview
                var url = $"/{type}/{id}/{Overview}";
                sb.AppendLine($"- [{dep}]({url})");
            }
            else
            {
                sb.AppendLine($"- {dep}");
            }
        }

        return Controls.Markdown(sb.ToString());
    }

    private static string GetStatusIcon(ContentStatus status) => status switch
    {
        ContentStatus.Draft => "📝",
        ContentStatus.InReview => "👀",
        ContentStatus.Approved => "✅",
        ContentStatus.Scheduled => "📅",
        ContentStatus.Published => "🚀",
        ContentStatus.Archived => "📦",
        _ => "❓"
    };

    #endregion
}
