using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Messaging;

namespace MeshWeaver.Activities;

public static class ActivityRegistry
{
    public static MessageHubConfiguration AddActivities(this MessageHubConfiguration configuration)
        => configuration.WithServices(services => services.AddSingleton<IActivityService, ActivityService>());

}