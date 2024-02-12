using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;

namespace OpenSmc.Activities;

public static class ActivityRegistry
{
    public static MessageHubConfiguration AddActivities(this MessageHubConfiguration configuration)
        => configuration.WithServices(services => services.AddSingleton<IActivityService, ActivityService>());

}