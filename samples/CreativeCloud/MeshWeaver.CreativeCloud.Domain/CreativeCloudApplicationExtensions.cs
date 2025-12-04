using MeshWeaver.CreativeCloud.Domain.LayoutAreas;
using MeshWeaver.CreativeCloud.Domain.SampleData;
using MeshWeaver.Data;
using MeshWeaver.Layout;
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
}
