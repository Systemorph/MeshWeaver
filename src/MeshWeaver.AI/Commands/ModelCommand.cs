#nullable enable

using System.Text.RegularExpressions;

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Command to switch the current AI model.
/// Usage: /model @model/ModelName or /model ModelName
/// </summary>
public class ModelCommand : IChatCommand
{
    public string Name => "model";
    public string Description => "Switch to a different AI model for subsequent messages";
    public string Usage => "/model @model/Name or /model Name";

    private static readonly Regex ModelRefPattern =
        new(@"@model/(.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        if (context.ParsedCommand.Arguments.Length == 0)
        {
            // List available models grouped by provider
            if (context.AvailableModels == null || !context.AvailableModels.Any())
            {
                return Task.FromResult(CommandResult.Error(
                    $"Usage: {Usage}\n\nNo models available."));
            }

            var grouped = context.AvailableModels
                .GroupBy(m => m.Provider)
                .OrderBy(g => g.First().DisplayOrder);

            var modelList = string.Join("\n", grouped.Select(g =>
                $"**{g.Key}**: {string.Join(", ", g.Select(m => m.Name))}"));

            return Task.FromResult(CommandResult.Error(
                $"Usage: {Usage}\n\nAvailable models:\n{modelList}"));
        }

        // Parse model name from argument
        var arg = context.ParsedCommand.RawArguments;
        string modelName;

        var match = ModelRefPattern.Match(arg);
        if (match.Success)
        {
            modelName = match.Groups[1].Value;
        }
        else
        {
            // Allow just the model name without @model/ prefix
            modelName = context.ParsedCommand.RawArguments.Trim();
        }

        // Find the model (case-insensitive)
        if (context.AvailableModels == null || !context.AvailableModels.Any())
        {
            return Task.FromResult(CommandResult.Error("No models available."));
        }

        var found = context.AvailableModels
            .FirstOrDefault(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase));

        if (found == null)
        {
            var grouped = context.AvailableModels
                .GroupBy(m => m.Provider)
                .OrderBy(g => g.First().DisplayOrder);

            var modelList = string.Join("\n", grouped.Select(g =>
                $"**{g.Key}**: {string.Join(", ", g.Select(m => m.Name))}"));

            return Task.FromResult(CommandResult.Error(
                $"Model '{modelName}' not found.\n\nAvailable models:\n{modelList}"));
        }

        // Switch to the model
        context.SetCurrentModel?.Invoke(found);

        return Task.FromResult(CommandResult.Ok(
            $"Switched to model: **{found.Name}** ({found.Provider})"));
    }
}
