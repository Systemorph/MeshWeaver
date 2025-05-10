using MeshWeaver.Reinsurance.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;
public static class AIExtensions
{
    public static IServiceCollection AddAI(this IServiceCollection serviceCollection, Func<AIConfiguration, AIConfiguration> configuration = null)
        => serviceCollection.AddSingleton<IChatService, ChatService>()
            .AddSingleton((configuration ??(x=>x)).Invoke(new()));
}

