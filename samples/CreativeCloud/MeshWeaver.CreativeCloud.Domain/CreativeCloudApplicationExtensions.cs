using MeshWeaver.CreativeCloud.Domain.LayoutAreas;
using MeshWeaver.CreativeCloud.Domain.SampleData;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Extensions for configuring the CreativeCloud application.
/// </summary>
public static class CreativeCloudApplicationExtensions
{
    /// <summary>
    /// Configures the CreativeCloud application hub.
    /// </summary>
    /// <param name="configuration">The message hub configuration.</param>
    /// <returns>The configured message hub configuration.</returns>
    public static MessageHubConfiguration ConfigureCreativeCloudApplication(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithTypes(typeof(ContentStatus))
            .WithTypes(new KeyValuePair<string, Type>[] {
                new(PersonAddress.TypeName, typeof(PersonAddress)),
                new(ArchAddress.TypeName, typeof(ArchAddress)),
                new(StoryAddress.TypeName, typeof(StoryAddress)),
                new(VideoAddress.TypeName, typeof(VideoAddress)),
                new(EventAddress.TypeName, typeof(EventAddress)),
                new(PostAddress.TypeName, typeof(PostAddress))
            })
            .AddData(data => data
                // Reference data
                .AddSource(src => src
                    .WithType<ContentArchetype>(t => t
                        .WithKey(x => x.Id)
                        .WithInitialData(CreativeCloudSampleData.GetContentArchetypes()))
                )
                .AddSource(src => src
                    .WithType<ContentLens>(t => t
                        .WithKey(x => x.Id)
                        .WithInitialData(CreativeCloudSampleData.GetContentLenses()))
                )
                .AddSource(src => src
                    .WithType<Person>(t => t
                        .WithKey(x => x.Id)
                        .WithInitialData(CreativeCloudSampleData.GetPersons()))
                )
                .AddSource(src => src
                    .WithType<StoryArch>(t => t
                        .WithKey(x => x.Id)
                        .WithInitialData(CreativeCloudSampleData.GetStoryArches()))
                )
                // Content data
                .AddSource(src => src
                    .WithType<Story>(t => t
                        .WithKey(x => x.Id)
                        .WithInitialData(CreativeCloudSampleData.GetStories()))
                )
                .AddSource(src => src
                    .WithType<Post>(t => t
                        .WithKey(x => x.Id)
                        .WithInitialData(CreativeCloudSampleData.GetPosts()))
                )
                .AddSource(src => src
                    .WithType<Video>(t => t
                        .WithKey(x => x.Id)
                        .WithInitialData(CreativeCloudSampleData.GetVideos()))
                )
                .AddSource(src => src
                    .WithType<Event>(t => t
                        .WithKey(x => x.Id)
                        .WithInitialData(CreativeCloudSampleData.GetEvents()))
                )
            )
            .AddLayout(layout => layout
                .WithView(nameof(CreativeCloudLayoutAreas.Dashboard), CreativeCloudLayoutAreas.Dashboard)
                .WithView(nameof(CreativeCloudLayoutAreas.Stories), CreativeCloudLayoutAreas.Stories)
                .WithView(nameof(CreativeCloudLayoutAreas.Posts), CreativeCloudLayoutAreas.Posts)
                .WithView(nameof(CreativeCloudLayoutAreas.Videos), CreativeCloudLayoutAreas.Videos)
                .WithView(nameof(CreativeCloudLayoutAreas.Events), CreativeCloudLayoutAreas.Events)
                .WithView(nameof(CreativeCloudLayoutAreas.Persons), CreativeCloudLayoutAreas.Persons)
                .WithView(nameof(CreativeCloudLayoutAreas.StoryArches), CreativeCloudLayoutAreas.StoryArches)
                .WithView(nameof(CreativeCloudLayoutAreas.ContentArchetypes), CreativeCloudLayoutAreas.ContentArchetypes)
                .WithView(nameof(CreativeCloudLayoutAreas.ContentLenses), CreativeCloudLayoutAreas.ContentLenses)
            //.WithThumbnailBasePath("/app/CreativeCloud/static/CreativeCloud/thumbnails")
            );
    }

    /// <summary>
    /// Configures a Story detail hub with Overview, Workflow, Dependencies views.
    /// URL: /story/{storyId}/{Area}
    /// </summary>
    public static MessageHubConfiguration ConfigureStoryHub(this MessageHubConfiguration configuration)
        => configuration.ConfigureEntityHub<Story>(
            EntityDetailsLayoutAreas.StoryOverview,
            EntityDetailsLayoutAreas.StoryWorkflow,
            EntityDetailsLayoutAreas.StoryDependencies);

    /// <summary>
    /// Configures a Post detail hub with Overview, Workflow, Dependencies views.
    /// URL: /post/{postId}/{Area}
    /// </summary>
    public static MessageHubConfiguration ConfigurePostHub(this MessageHubConfiguration configuration)
        => configuration.ConfigureEntityHub<Post>(
            EntityDetailsLayoutAreas.PostOverview,
            EntityDetailsLayoutAreas.PostWorkflow,
            EntityDetailsLayoutAreas.PostDependencies);

    /// <summary>
    /// Configures a Video detail hub with Overview, Workflow, Dependencies views.
    /// URL: /video/{videoId}/{Area}
    /// </summary>
    public static MessageHubConfiguration ConfigureVideoHub(this MessageHubConfiguration configuration)
        => configuration.ConfigureEntityHub<Video>(
            EntityDetailsLayoutAreas.VideoOverview,
            EntityDetailsLayoutAreas.VideoWorkflow,
            EntityDetailsLayoutAreas.VideoDependencies);

    /// <summary>
    /// Configures an Event detail hub with Overview, Workflow, Dependencies views.
    /// URL: /event/{eventId}/{Area}
    /// </summary>
    public static MessageHubConfiguration ConfigureEventHub(this MessageHubConfiguration configuration)
        => configuration.ConfigureEntityHub<Event>(
            EntityDetailsLayoutAreas.EventOverview,
            EntityDetailsLayoutAreas.EventWorkflow,
            EntityDetailsLayoutAreas.EventDependencies);

    /// <summary>
    /// Configures a Person detail hub with Overview and Dependencies views.
    /// URL: /person/{personId}/{Area}
    /// </summary>
    public static MessageHubConfiguration ConfigurePersonHub(this MessageHubConfiguration configuration)
        => configuration.ConfigureEntityHub<Person>(
            EntityDetailsLayoutAreas.PersonOverview,
            null,
            EntityDetailsLayoutAreas.PersonDependencies);

    /// <summary>
    /// Configures an Arch (StoryArch) detail hub with Overview and Dependencies views.
    /// URL: /arch/{archId}/{Area}
    /// </summary>
    public static MessageHubConfiguration ConfigureArchHub(this MessageHubConfiguration configuration)
        => configuration.ConfigureEntityHub<StoryArch>(
            EntityDetailsLayoutAreas.ArchOverview,
            null,
            EntityDetailsLayoutAreas.ArchDependencies);

    /// <summary>
    /// Common configuration for entity detail hubs.
    /// </summary>
    private static MessageHubConfiguration ConfigureEntityHub<T>(
        this MessageHubConfiguration configuration,
        Func<LayoutAreaHost, RenderingContext, IObservable<UiControl?>> overviewView,
        Func<LayoutAreaHost, RenderingContext, IObservable<UiControl?>>? workflowView,
        Func<LayoutAreaHost, RenderingContext, IObservable<UiControl?>> dependenciesView)
        where T : class
    {
        var layout = configuration
            .WithTypes(typeof(T))
            .AddData(data => data
                .AddSource(src => src
                    .WithType<T>(t => t.WithInitialData(_ => Task.FromResult(Enumerable.Empty<T>())))
                )
            )
            .AddLayout(l =>
            {
                l = l.WithDefaultArea(EntityDetailsLayoutAreas.Overview)
                     .WithView(EntityDetailsLayoutAreas.Overview, overviewView)
                     .WithView(EntityDetailsLayoutAreas.Dependencies, dependenciesView);

                if (workflowView != null)
                    l = l.WithView(EntityDetailsLayoutAreas.Workflow, workflowView);

                return l;
            });

        return layout;
    }
}
