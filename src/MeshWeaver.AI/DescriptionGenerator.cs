using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive;
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

    /// <summary>
    /// Creates the generator, resolving an optional logger from <paramref name="services"/>.
    /// The same provider is used to spin up an <c>AgentChatClient</c> per generation call.
    /// </summary>
    /// <param name="services">The service provider used to resolve the logger and build per-call chat clients.</param>
    public DescriptionGenerator(IServiceProvider services)
    {
        this.services = services;
        this.logger = (ILogger<DescriptionGenerator>?)services.GetService(typeof(ILogger<DescriptionGenerator>));
    }

    /// <summary>
    /// Generates a short human-readable description for a node by running the built-in
    /// <c>DescriptionWriter</c> agent over the supplied name (and optional category) and
    /// parsing the <c>Description:</c> line from its reply.
    /// </summary>
    /// <param name="name">The node name to describe.</param>
    /// <param name="category">Optional category hint that steers the generated wording; may be <c>null</c>.</param>
    /// <param name="ct">Token to cancel the underlying agent call.</param>
    /// <returns>A cold observable that emits the parsed description, or errors if the agent returns no parsable description.</returns>
    public IObservable<string> GenerateDescriptionAsync(string name, string? category, CancellationToken ct = default)
    {
        var chat = new AgentChatClient(services);
        chat.Initialize(contextPath: "Agent");
        return chat.WhenInitialized.Take(1).SelectMany(_ =>
        {
            chat.SetSelectedAgent("DescriptionWriter");
            var prompt = BuildPrompt(name, category);
            var messages = new[] { new ChatMessage(ChatRole.User, prompt) };
            return chat.GetResponseAsync(messages, ct).ToObservableSequence()
                .Aggregate(new StringBuilder(), (sb, msg) =>
                {
                    foreach (var content in msg.Contents.OfType<TextContent>())
                        sb.Append(content.Text);
                    return sb;
                })
                .Select(sb =>
                {
                    var raw = sb.ToString();
                    var description = ExtractDescription(raw);
                    if (string.IsNullOrEmpty(description))
                    {
                        logger?.LogWarning("DescriptionWriter response did not contain a parsable Description line. Raw: {Raw}", raw);
                        throw new InvalidOperationException("Agent did not return a description.");
                    }
                    return description;
                });
        });
    }

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
