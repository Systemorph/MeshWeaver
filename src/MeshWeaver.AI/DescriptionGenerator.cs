using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Default <see cref="IDescriptionGenerator"/> — spins up a fresh <see cref="AgentChatClient"/>
/// per call, selects the built-in <c>DescriptionWriter</c> agent, sends a single user
/// message, and parses the <c>Description:</c> line from the response.
/// </summary>
public sealed class DescriptionGenerator : IDescriptionGenerator
{
    private readonly IServiceProvider services;
    private readonly ILogger<DescriptionGenerator>? logger;

    public DescriptionGenerator(IServiceProvider services)
    {
        this.services = services;
        this.logger = (ILogger<DescriptionGenerator>?)services.GetService(typeof(ILogger<DescriptionGenerator>));
    }

    public IObservable<string> GenerateDescriptionAsync(string name, string? category, CancellationToken ct = default)
        => Observable.FromAsync(async cancellation =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellation);
            var token = linked.Token;

            var chat = new AgentChatClient(services);
            await chat.InitializeAsync(contextPath: "Agent");
            chat.SetSelectedAgent("DescriptionWriter");

            var prompt = BuildPrompt(name, category);
            var messages = new[] { new ChatMessage(ChatRole.User, prompt) };

            var sb = new StringBuilder();
            await foreach (var msg in chat.GetResponseAsync(messages, token))
            {
                foreach (var content in msg.Contents.OfType<TextContent>())
                    sb.Append(content.Text);
            }

            var raw = sb.ToString();
            var description = ExtractDescription(raw);
            if (string.IsNullOrEmpty(description))
            {
                logger?.LogWarning("DescriptionWriter response did not contain a parsable Description line. Raw: {Raw}", raw);
                throw new InvalidOperationException("Agent did not return a description.");
            }
            return description;
        });

    private static string BuildPrompt(string name, string? category)
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim();
        if (string.IsNullOrWhiteSpace(category))
            return $"Name: {safeName}";
        return $"Name: {safeName}\nCategory: {category.Trim()}";
    }

    // Matches the "Description: <...>" line in the DescriptionWriter response block.
    private static readonly Regex DescriptionLineRegex = new(
        @"(?im)^\s*Description:\s*(.+?)\s*$",
        RegexOptions.Compiled);

    private static string? ExtractDescription(string text)
    {
        var match = DescriptionLineRegex.Match(text);
        if (match.Success)
            return match.Groups[1].Value.Trim().Trim('"');

        // Fallback: first non-empty line, stripped of any leading "Description:" marker.
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;
            if (trimmed.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed["Description:".Length..].Trim();
            return trimmed.Trim('"');
        }
        return null;
    }
}
