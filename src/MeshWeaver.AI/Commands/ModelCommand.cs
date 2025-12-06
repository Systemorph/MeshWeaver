#nullable enable

using System.Text.RegularExpressions;

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Command to switch the current AI model.
/// Usage: /model @model:ModelName or /model ModelName
/// </summary>
public class ModelCommand : IChatCommand
{
    public string Name => "model";
    public string Description => "Switch to a different AI model for subsequent messages";
    public string Usage => "/model @model:Name or /model Name";

    private static readonly Regex ModelRefPattern =
        new(@"@model:(.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        if (context.ParsedCommand.Arguments.Length == 0)
        {
            // List available models
            var modelNames = context.AvailableModels != null
                ? string.Join(", ", context.AvailableModels.OrderBy(m => m))
                : "No models available";
            return Task.FromResult(CommandResult.Error(
                $"Usage: {Usage}\n\nAvailable models: {modelNames}"));
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
            // Allow just the model name without model: prefix
            modelName = context.ParsedCommand.RawArguments.Trim();
        }

        // Find the model (case-insensitive)
        if (context.AvailableModels == null || !context.AvailableModels.Any())
        {
            return Task.FromResult(CommandResult.Error("No models available."));
        }

        var found = context.AvailableModels
            .FirstOrDefault(m => m.Equals(modelName, StringComparison.OrdinalIgnoreCase));

        if (found == null)
        {
            var availableNames = string.Join(", ", context.AvailableModels.OrderBy(m => m));
            return Task.FromResult(CommandResult.Error(
                $"Model '{modelName}' not found.\n\nAvailable models: {availableNames}"));
        }

        // Switch to the model
        context.SetCurrentModel?.Invoke(found);

        return Task.FromResult(CommandResult.Ok(
            $"Switched to model: **{found}**"));
    }
}
