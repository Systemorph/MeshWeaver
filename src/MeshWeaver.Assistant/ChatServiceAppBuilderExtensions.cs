using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using static System.Environment;

namespace MeshWeaver.Assistant;

#pragma warning disable AOAI001

public static class ChatServiceAppBuilderExtensions
{
    public static IHostApplicationBuilder AddChatService(
        this IHostApplicationBuilder builder)
    {
        var azureOpenAiEndpoint = GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureOpenAiKey = GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deploymentName = GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_ID");

        builder.Services.AddScoped(_ =>
            new AzureOpenAIClient(
                new Uri(azureOpenAiEndpoint),
                new AzureKeyCredential(azureOpenAiKey)).GetChatClient(deploymentName)
        );

        builder.Services.AddScoped<ChatService>();

        return builder;
    }
}
