using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.CreativeCloud.Domain.LayoutAreas;

/// <summary>
/// Layout areas for the CreativeCloud application.
/// </summary>
public static class CreativeCloudLayoutAreas
{
    /// <summary>
    /// Creates a Stories layout area showing all stories with their metadata.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable UI control that updates when stories change.</returns>
    [Display(GroupName = "1. Content", Order = 0)]
    public static IObservable<UiControl?> Stories(LayoutAreaHost host, RenderingContext context)
    {
        _ = context;
        return host.Workspace
            .GetStream<Story>()!
            .Select(stories => CreateStoriesView(stories!, host))
            .StartWith(Controls.Markdown("# Stories\n\n*Loading stories...*"));
    }

    /// <summary>
    /// Creates a Posts layout area showing all social media posts.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable UI control that updates when posts change.</returns>
    [Display(GroupName = "1. Content", Order = 1)]
    public static IObservable<UiControl?> Posts(LayoutAreaHost host, RenderingContext context)
    {
        _ = context;
        return host.Workspace
            .GetStream<Post>()!
            .Select(posts => CreatePostsView(posts!, host))
            .StartWith(Controls.Markdown("# Posts\n\n*Loading posts...*"));
    }

    /// <summary>
    /// Creates a Videos layout area showing all video content.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable UI control that updates when videos change.</returns>
    [Display(GroupName = "1. Content", Order = 2)]
    public static IObservable<UiControl?> Videos(LayoutAreaHost host, RenderingContext context)
    {
        _ = context;
        return host.Workspace
            .GetStream<Video>()!
            .Select(videos => CreateVideosView(videos!, host))
            .StartWith(Controls.Markdown("# Videos\n\n*Loading videos...*"));
    }

    /// <summary>
    /// Creates an Events layout area showing all events.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable UI control that updates when events change.</returns>
    [Display(GroupName = "1. Content", Order = 3)]
    public static IObservable<UiControl?> Events(LayoutAreaHost host, RenderingContext context)
    {
        _ = context;
        return host.Workspace
            .GetStream<Event>()!
            .Select(events => CreateEventsView(events!, host))
            .StartWith(Controls.Markdown("# Events\n\n*Loading events...*"));
    }

    /// <summary>
    /// Creates a Persons layout area showing all people/authors.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable UI control that updates when persons change.</returns>
    [Display(GroupName = "2. Reference Data", Order = 0)]
    public static IObservable<UiControl?> Persons(LayoutAreaHost host, RenderingContext context)
    {
        _ = context;
        return host.Workspace
            .GetStream<Person>()!
            .Select(persons => CreatePersonsView(persons!, host))
            .StartWith(Controls.Markdown("# People\n\n*Loading people...*"));
    }

    /// <summary>
    /// Creates a StoryArches layout area showing all story arches.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable UI control that updates when story arches change.</returns>
    [Display(GroupName = "2. Reference Data", Order = 1)]
    public static IObservable<UiControl?> StoryArches(LayoutAreaHost host, RenderingContext context)
    {
        _ = context;
        return host.Workspace
            .GetStream<StoryArch>()!
            .Select(arches => CreateStoryArchesView(arches!, host))
            .StartWith(Controls.Markdown("# Story Arches\n\n*Loading story arches...*"));
    }

    /// <summary>
    /// Creates a ContentArchetypes layout area showing content archetypes.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable UI control that updates when content archetypes change.</returns>
    [Display(GroupName = "2. Reference Data", Order = 2)]
    public static IObservable<UiControl?> ContentArchetypes(LayoutAreaHost host, RenderingContext context)
    {
        _ = context;
        return host.Workspace
            .GetStream<ContentArchetype>()!
            .Select(archetypes => CreateContentArchetypesView(archetypes!, host))
            .StartWith(Controls.Markdown("# Content Archetypes\n\n*Loading content archetypes...*"));
    }

    /// <summary>
    /// Creates a ContentLenses layout area showing content lenses.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable UI control that updates when content lenses change.</returns>
    [Display(GroupName = "2. Reference Data", Order = 3)]
    public static IObservable<UiControl?> ContentLenses(LayoutAreaHost host, RenderingContext context)
    {
        _ = context;
        return host.Workspace
            .GetStream<ContentLens>()!
            .Select(lenses => CreateContentLensesView(lenses!, host))
            .StartWith(Controls.Markdown("# Content Lenses\n\n*Loading content lenses...*"));
    }

    /// <summary>
    /// Creates a Dashboard layout area showing an overview of all content.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable UI control showing dashboard statistics.</returns>
    [Display(GroupName = "0. Overview", Order = 0)]
    public static IObservable<UiControl?> Dashboard(LayoutAreaHost host, RenderingContext context)
    {
        _ = context;
        // Combine multiple streams for the dashboard
        var storiesStream = (host.Workspace.GetStream<Story>() ?? Observable.Return(Array.Empty<Story>()))
            .Select(s => (IReadOnlyCollection<Story>)(s ?? Array.Empty<Story>()));
        var postsStream = (host.Workspace.GetStream<Post>() ?? Observable.Return(Array.Empty<Post>()))
            .Select(p => (IReadOnlyCollection<Post>)(p ?? Array.Empty<Post>()));
        var videosStream = (host.Workspace.GetStream<Video>() ?? Observable.Return(Array.Empty<Video>()))
            .Select(v => (IReadOnlyCollection<Video>)(v ?? Array.Empty<Video>()));
        var eventsStream = (host.Workspace.GetStream<Event>() ?? Observable.Return(Array.Empty<Event>()))
            .Select(e => (IReadOnlyCollection<Event>)(e ?? Array.Empty<Event>()));

        return storiesStream
            .CombineLatest(postsStream, videosStream, eventsStream, (stories, posts, videos, events) =>
                CreateDashboardView(stories, posts, videos, events, host))
            .StartWith(Controls.Markdown("# CreativeCloud Dashboard\n\n*Loading dashboard...*"));
    }

    #region View Creation Methods

    private static UiControl? CreateStoriesView(IReadOnlyCollection<Story> stories, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("Stories")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        if (!stories.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No stories found.*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        // Group by status
        var statusGroups = stories.GroupBy(s => s.Status).OrderBy(g => (int)g.Key);

        foreach (var group in statusGroups)
        {
            var statusIcon = GetStatusIcon(group.Key);
            mainGrid = mainGrid
                .WithView(Controls.H5($"{statusIcon} {group.Key} ({group.Count()})")
                    .WithStyle(style => style.WithMarginTop("16px").WithMarginBottom("8px")),
                    skin => skin.WithXs(12));

            foreach (var story in group.OrderByDescending(s => s.CreatedAt))
            {
                var sb = new StringBuilder();
                sb.AppendLine($"**{story.Title}**");
                if (!string.IsNullOrEmpty(story.StoryArchId))
                    sb.AppendLine($"*Arch: {story.StoryArchId}*");
                if (story.CreatedAt.HasValue)
                    sb.AppendLine($"Created: {story.CreatedAt.Value:yyyy-MM-dd}");

                mainGrid = mainGrid
                    .WithView(Controls.Markdown(sb.ToString())
                        .WithStyle(style => style
                            .WithPadding("12px")
                            .WithMarginBottom("8px")
                            .WithBorder("1px solid var(--color-border-default)")
                            .WithBorderRadius("6px")
                            .WithBackgroundColor("var(--color-canvas-subtle)")),
                        skin => skin.WithXs(12));
            }
        }

        return mainGrid;
    }

    private static UiControl? CreatePostsView(IReadOnlyCollection<Post> posts, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("Social Media Posts")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        if (!posts.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No posts found.*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        // Group by platform
        var platformGroups = posts.GroupBy(p => p.Platform ?? "Unknown").OrderBy(g => g.Key);

        foreach (var group in platformGroups)
        {
            mainGrid = mainGrid
                .WithView(Controls.H5($"{group.Key} ({group.Count()})")
                    .WithStyle(style => style.WithMarginTop("16px").WithMarginBottom("8px")),
                    skin => skin.WithXs(12));

            foreach (var post in group.OrderByDescending(p => p.ScheduledAt ?? p.PublishedAt))
            {
                var statusIcon = GetStatusIcon(post.Status);
                var pillarBadge = !string.IsNullOrEmpty(post.ContentPillar) ? $"`{post.ContentPillar}`" : "";

                var sb = new StringBuilder();
                sb.AppendLine($"{statusIcon} **{post.Title}** {pillarBadge}");
                if (post.ScheduledAt.HasValue)
                    sb.AppendLine($"Scheduled: {post.ScheduledAt.Value:yyyy-MM-dd HH:mm}");
                if (post.PublishedAt.HasValue)
                    sb.AppendLine($"Published: {post.PublishedAt.Value:yyyy-MM-dd HH:mm}");

                mainGrid = mainGrid
                    .WithView(Controls.Markdown(sb.ToString())
                        .WithStyle(style => style
                            .WithPadding("12px")
                            .WithMarginBottom("8px")
                            .WithBorder("1px solid var(--color-border-default)")
                            .WithBorderRadius("6px")
                            .WithBackgroundColor("var(--color-canvas-subtle)")),
                        skin => skin.WithXs(12));
            }
        }

        return mainGrid;
    }

    private static UiControl? CreateVideosView(IReadOnlyCollection<Video> videos, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("Videos")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        if (!videos.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No videos found.*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        // Group by platform
        var platformGroups = videos.GroupBy(v => v.Platform ?? "Unknown").OrderBy(g => g.Key);

        foreach (var group in platformGroups)
        {
            mainGrid = mainGrid
                .WithView(Controls.H5($"{group.Key} ({group.Count()})")
                    .WithStyle(style => style.WithMarginTop("16px").WithMarginBottom("8px")),
                    skin => skin.WithXs(12));

            foreach (var video in group)
            {
                var statusIcon = GetStatusIcon(video.Status);
                var duration = video.DurationSeconds.HasValue
                    ? $"{video.DurationSeconds / 60}:{video.DurationSeconds % 60:D2}"
                    : "N/A";

                var sb = new StringBuilder();
                sb.AppendLine($"{statusIcon} **{video.Title}**");
                sb.AppendLine($"Duration: {duration}");
                if (!string.IsNullOrEmpty(video.Description))
                    sb.AppendLine($"*{video.Description}*");

                mainGrid = mainGrid
                    .WithView(Controls.Markdown(sb.ToString())
                        .WithStyle(style => style
                            .WithPadding("12px")
                            .WithMarginBottom("8px")
                            .WithBorder("1px solid var(--color-border-default)")
                            .WithBorderRadius("6px")
                            .WithBackgroundColor("var(--color-canvas-subtle)")),
                        skin => skin.WithXs(12));
            }
        }

        return mainGrid;
    }

    private static UiControl? CreateEventsView(IReadOnlyCollection<Event> events, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("Events")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        if (!events.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No events found.*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        // Group by event type
        var typeGroups = events.GroupBy(e => e.EventType ?? "Other").OrderBy(g => g.Key);

        foreach (var group in typeGroups)
        {
            mainGrid = mainGrid
                .WithView(Controls.H5($"{group.Key} ({group.Count()})")
                    .WithStyle(style => style.WithMarginTop("16px").WithMarginBottom("8px")),
                    skin => skin.WithXs(12));

            foreach (var evt in group.OrderBy(e => e.StartDate))
            {
                var statusIcon = GetStatusIcon(evt.Status);

                var sb = new StringBuilder();
                sb.AppendLine($"{statusIcon} **{evt.Title}**");
                if (evt.StartDate.HasValue)
                    sb.AppendLine($"Date: {evt.StartDate.Value:yyyy-MM-dd HH:mm}");
                if (!string.IsNullOrEmpty(evt.Location))
                    sb.AppendLine($"Location: {evt.Location}");
                if (!string.IsNullOrEmpty(evt.VirtualUrl))
                    sb.AppendLine($"Virtual: {evt.VirtualUrl}");

                mainGrid = mainGrid
                    .WithView(Controls.Markdown(sb.ToString())
                        .WithStyle(style => style
                            .WithPadding("12px")
                            .WithMarginBottom("8px")
                            .WithBorder("1px solid var(--color-border-default)")
                            .WithBorderRadius("6px")
                            .WithBackgroundColor("var(--color-canvas-subtle)")),
                        skin => skin.WithXs(12));
            }
        }

        return mainGrid;
    }

    private static UiControl? CreatePersonsView(IReadOnlyCollection<Person> persons, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("People")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        if (!persons.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No people found.*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        foreach (var person in persons.OrderBy(p => p.LastName).ThenBy(p => p.FirstName))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**{person.FirstName} {person.LastName}**");
            if (!string.IsNullOrEmpty(person.Company))
                sb.AppendLine($"Company: {person.Company}");
            if (!string.IsNullOrEmpty(person.Email))
                sb.AppendLine($"Email: {person.Email}");
            if (!string.IsNullOrEmpty(person.ContentArchetypeId))
                sb.AppendLine($"*Archetype: {person.ContentArchetypeId}*");

            mainGrid = mainGrid
                .WithView(Controls.Markdown(sb.ToString())
                    .WithStyle(style => style
                        .WithPadding("12px")
                        .WithMarginBottom("8px")
                        .WithBorder("1px solid var(--color-border-default)")
                        .WithBorderRadius("6px")
                        .WithBackgroundColor("var(--color-canvas-subtle)")),
                    skin => skin.WithXs(12).WithSm(6).WithMd(4));
        }

        return mainGrid;
    }

    private static UiControl? CreateStoryArchesView(IReadOnlyCollection<StoryArch> arches, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("Story Arches")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        if (!arches.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No story arches found.*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        foreach (var arch in arches)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**{arch.Name}**");
            if (!string.IsNullOrEmpty(arch.Theme))
                sb.AppendLine($"Theme: {arch.Theme}");
            if (!string.IsNullOrEmpty(arch.Description))
                sb.AppendLine($"*{arch.Description}*");

            mainGrid = mainGrid
                .WithView(Controls.Markdown(sb.ToString())
                    .WithStyle(style => style
                        .WithPadding("12px")
                        .WithMarginBottom("8px")
                        .WithBorder("1px solid var(--color-border-default)")
                        .WithBorderRadius("6px")
                        .WithBackgroundColor("var(--color-canvas-subtle)")),
                    skin => skin.WithXs(12).WithSm(6));
        }

        return mainGrid;
    }

    private static UiControl? CreateContentArchetypesView(IReadOnlyCollection<ContentArchetype> archetypes, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("Content Archetypes")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        if (!archetypes.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No content archetypes found.*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        foreach (var archetype in archetypes)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## {archetype.Name}");
            sb.AppendLine();
            sb.AppendLine($"**Purpose:** {archetype.PurposeStatement}");
            sb.AppendLine();
            sb.AppendLine("### Content Pillars");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(archetype.TacticalDescription))
                sb.AppendLine($"- **Tactical:** {archetype.TacticalDescription}");
            if (!string.IsNullOrEmpty(archetype.AspirationalDescription))
                sb.AppendLine($"- **Aspirational:** {archetype.AspirationalDescription}");
            if (!string.IsNullOrEmpty(archetype.InsightfulDescription))
                sb.AppendLine($"- **Insightful:** {archetype.InsightfulDescription}");
            if (!string.IsNullOrEmpty(archetype.PersonalDescription))
                sb.AppendLine($"- **Personal:** {archetype.PersonalDescription}");

            mainGrid = mainGrid
                .WithView(Controls.Markdown(sb.ToString())
                    .WithStyle(style => style
                        .WithPadding("16px")
                        .WithMarginBottom("12px")
                        .WithBorder("1px solid var(--color-border-default)")
                        .WithBorderRadius("8px")
                        .WithBackgroundColor("var(--color-canvas-subtle)")),
                    skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    private static UiControl? CreateContentLensesView(IReadOnlyCollection<ContentLens> lenses, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("Content Lenses")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        if (!lenses.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No content lenses found.*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        // Group by pillar
        var pillarGroups = lenses.GroupBy(l => l.Pillar).OrderBy(g => g.Key);

        foreach (var group in pillarGroups)
        {
            var pillarColor = group.Key switch
            {
                "Tactical" => "var(--color-accent-fg)",
                "Aspirational" => "var(--color-success-fg)",
                "Insightful" => "var(--color-warning-fg)",
                "Personal" => "var(--color-danger-fg)",
                _ => "var(--color-fg-default)"
            };

            mainGrid = mainGrid
                .WithView(Controls.H5($"{group.Key} ({group.Count()})")
                    .WithStyle(style => style.WithMarginTop("16px").WithMarginBottom("8px").WithColor(pillarColor)),
                    skin => skin.WithXs(12));

            foreach (var lens in group.OrderBy(l => l.DisplayOrder))
            {
                var sb = new StringBuilder();
                sb.AppendLine($"**{lens.Name}**");
                if (!string.IsNullOrEmpty(lens.Description))
                    sb.AppendLine($"*{lens.Description}*");

                mainGrid = mainGrid
                    .WithView(Controls.Markdown(sb.ToString())
                        .WithStyle(style => style
                            .WithPadding("12px")
                            .WithMarginBottom("8px")
                            .WithBorder("1px solid var(--color-border-default)")
                            .WithBorderRadius("6px")
                            .WithBackgroundColor("var(--color-canvas-subtle)")),
                        skin => skin.WithXs(12).WithSm(6).WithMd(4));
            }
        }

        return mainGrid;
    }

    private static UiControl? CreateDashboardView(
        IReadOnlyCollection<Story> stories,
        IReadOnlyCollection<Post> posts,
        IReadOnlyCollection<Video> videos,
        IReadOnlyCollection<Event> events,
        LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("CreativeCloud Dashboard")
                .WithStyle(style => style.WithMarginBottom("16px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        // Content summary statistics
        mainGrid = mainGrid
            .WithView(Controls.H5("Content Overview")
                .WithStyle(style => style.WithMarginBottom("8px")),
                skin => skin.WithXs(12));

        // Stories stats
        var draftStories = stories.Count(s => s.Status == ContentStatus.Draft);
        var publishedStories = stories.Count(s => s.Status == ContentStatus.Published);
        mainGrid = mainGrid
            .WithView(Controls.Markdown($"**Stories:** {stories.Count} total ({draftStories} draft, {publishedStories} published)")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                skin => skin.WithXs(12));

        // Posts stats
        var scheduledPosts = posts.Count(p => p.Status == ContentStatus.Scheduled);
        var publishedPosts = posts.Count(p => p.Status == ContentStatus.Published);
        mainGrid = mainGrid
            .WithView(Controls.Markdown($"**Posts:** {posts.Count} total ({scheduledPosts} scheduled, {publishedPosts} published)")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                skin => skin.WithXs(12));

        // Videos stats
        var draftVideos = videos.Count(v => v.Status == ContentStatus.Draft);
        var publishedVideos = videos.Count(v => v.Status == ContentStatus.Published);
        mainGrid = mainGrid
            .WithView(Controls.Markdown($"**Videos:** {videos.Count} total ({draftVideos} draft, {publishedVideos} published)")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                skin => skin.WithXs(12));

        // Events stats
        var upcomingEvents = events.Count(e => e.StartDate.HasValue && e.StartDate.Value > DateTime.Now);
        mainGrid = mainGrid
            .WithView(Controls.Markdown($"**Events:** {events.Count} total ({upcomingEvents} upcoming)")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                skin => skin.WithXs(12));

        // Status distribution
        mainGrid = mainGrid
            .WithView(Controls.H5("Status Distribution")
                .WithStyle(style => style.WithMarginTop("16px").WithMarginBottom("8px")),
                skin => skin.WithXs(12));

        var allStatuses = stories.Select(s => s.Status)
            .Concat(posts.Select(p => p.Status))
            .Concat(videos.Select(v => v.Status))
            .Concat(events.Select(e => e.Status))
            .GroupBy(s => s)
            .OrderBy(g => (int)g.Key);

        foreach (var group in allStatuses)
        {
            var statusIcon = GetStatusIcon(group.Key);
            mainGrid = mainGrid
                .WithView(Controls.Markdown($"{statusIcon} **{group.Key}:** {group.Count()}")
                    .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                    skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    #endregion

    #region Helper Methods

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
