using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Default <see cref="IIconGenerator"/> — spins up a fresh <see cref="AgentChatClient"/>
/// per call, selects the built-in <c>NodeInitializer</c> agent, sends a single user
/// message, and parses the <c>Svg:</c> line from the response.
/// </summary>
public sealed class IconGenerator : IIconGenerator
{
    private readonly IServiceProvider services;
    private readonly ILogger<IconGenerator>? logger;

    public IconGenerator(IServiceProvider services)
    {
        this.services = services;
        this.logger = (ILogger<IconGenerator>?)services.GetService(typeof(ILogger<IconGenerator>));
    }

    public IObservable<string> GenerateSvgAsync(string name, string? description, CancellationToken ct = default)
    {
        var chat = new AgentChatClient(services);
        // NodeInitializer is registered under the built-in "Agent" namespace.
        chat.Initialize(contextPath: "Agent");
        return chat.WhenInitialized.Take(1).SelectMany(_ =>
        {
            chat.SetSelectedAgent("NodeInitializer");
            var prompt = BuildPrompt(name, description);
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
                    var svg = ExtractSvg(raw);
                    if (string.IsNullOrEmpty(svg))
                    {
                        logger?.LogWarning("NodeInitializer response did not contain a parsable Svg line. Raw: {Raw}", raw);
                        throw new InvalidOperationException("Agent did not return an SVG.");
                    }
                    return svg;
                });
        });
    }

    private static string BuildPrompt(string name, string? description)
    {
        var desc = string.IsNullOrWhiteSpace(description)
            ? $"A node called \"{name}\"."
            : description.Trim();
        return $"Name: {name}\n\n{desc}";
    }

    // Matches the "Svg: <...>" line in the NodeInitializer response block.
    private static readonly Regex SvgLineRegex = new(
        @"(?im)^\s*Svg:\s*(<svg[\s\S]*?</svg>)\s*$",
        RegexOptions.Compiled);

    // Fallback: any <svg ...>...</svg> anywhere in the text.
    private static readonly Regex SvgAnyRegex = new(
        @"<svg[\s\S]*?</svg>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? ExtractSvg(string text)
    {
        var m = SvgLineRegex.Match(text);
        if (m.Success) return m.Groups[1].Value.Trim();
        var any = SvgAnyRegex.Match(text);
        return any.Success ? any.Value.Trim() : null;
    }
}
