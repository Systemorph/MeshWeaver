using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI.Chat;
using static System.Environment;

namespace MeshWeaver.Assistant;

#pragma warning disable AOAI001

public static class AssistantAppBuilderExtensions
{
    public static IHostApplicationBuilder AddAssistantService(
        this IHostApplicationBuilder builder)
    {
        var azureOpenAiEndpoint = GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureOpenAiKey = GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deploymentName = GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_ID");
        var searchEndpoint = GetEnvironmentVariable("AZURE_AI_SEARCH_ENDPOINT");
        var searchKey = GetEnvironmentVariable("AZURE_AI_SEARCH_API_KEY");
        var searchIndex = GetEnvironmentVariable("AZURE_AI_SEARCH_INDEX");

        builder.Services.AddScoped(_ =>
            new AzureOpenAIClient(
                new Uri(azureOpenAiEndpoint),
                new AzureKeyCredential(azureOpenAiKey)).GetChatClient(deploymentName)
        );

        builder.Services.AddScoped<AssistantService>();

        builder.Services.AddScoped(_ =>
        {
            ChatCompletionOptions options = new();

            options.AddDataSource(new AzureSearchChatDataSource()
            {
                Endpoint = new Uri(searchEndpoint),
                IndexName = searchIndex,
                Authentication = DataSourceAuthentication.FromApiKey(searchKey),
            });

            return options;
        });

        return builder;
    }
}
