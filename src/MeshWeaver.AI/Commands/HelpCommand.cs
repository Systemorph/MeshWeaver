#nullable enable

using System.Reflection;
using System.Text;

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Command to display help information about available commands.
/// </summary>
public class HelpCommand : IChatCommand
{
    public string Name => "help";
    public string Description => "Show available commands and their usage";
    public string Usage => "/help [command]";
    public IReadOnlyList<string> Aliases => ["?"];

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var registry = context.CommandRegistry;
        if (registry == null)
        {
            return Task.FromResult(CommandResult.Error("Command registry not available."));
        }

        var sb = new StringBuilder();

        if (context.ParsedCommand.Arguments.Length > 0)
        {
            // Show help for specific command
            var commandName = context.ParsedCommand.Arguments[0].TrimStart('/');
            if (registry.TryGetCommand(commandName, out var command) && command != null)
            {
                // Try to load help from markdown file
                var markdownHelp = LoadHelpMarkdown(command.Name);
                if (!string.IsNullOrEmpty(markdownHelp))
                {
                    sb.Append(markdownHelp);
                }
                else
                {
                    // Fallback to inline help
                    sb.AppendLine($"## /{command.Name}");
                    sb.AppendLine();
                    sb.AppendLine($"**Description:** {command.Description}");
                    sb.AppendLine();
                    sb.AppendLine($"**Usage:** `{command.Usage}`");

                    if (command.Aliases.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"**Aliases:** {string.Join(", ", command.Aliases.Select(a => "/" + a))}");
                    }
                }
            }
            else
            {
                return Task.FromResult(CommandResult.Error($"Unknown command: {commandName}"));
            }
        }
        else
        {
            // Show all commands
            sb.AppendLine("## Available Commands");
            sb.AppendLine();

            foreach (var command in registry.GetAllCommands().OrderBy(c => c.Name))
            {
                sb.AppendLine($"**/{command.Name}** - {command.Description}");
                sb.AppendLine($"  Usage: `{command.Usage}`");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine("**Tip:** You can also use `@agent/Name` anywhere in your message to address a specific agent.");
        }

        return Task.FromResult(CommandResult.Ok(sb.ToString()));
    }

    /// <summary>
    /// Tries to load help content from embedded markdown file.
    /// </summary>
    private static string? LoadHelpMarkdown(string commandName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"MeshWeaver.AI.Commands.Help.{commandName}.md";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return null;

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
