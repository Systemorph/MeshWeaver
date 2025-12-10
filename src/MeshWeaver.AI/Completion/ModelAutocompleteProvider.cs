#nullable enable

using MeshWeaver.Data.Completion;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Provides autocomplete items for AI models.
/// This is a local provider (no address routing) for the model/ prefix.
/// Items are only returned when explicitly requested (after /model command).
/// </summary>
public class ModelAutocompleteProvider : IAutocompleteProvider
{
    private IReadOnlyList<string> _availableModels = [];

    /// <summary>
    /// Sets the available models for autocomplete.
    /// Called when models are loaded or changed.
    /// </summary>
    public void SetAvailableModels(IReadOnlyList<string> models)
    {
        _availableModels = models;
    }

    /// <inheritdoc />
    public Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        var items = _availableModels
            .Select(model => new AutocompleteItem(
                Label: $"@model/{model}",
                InsertText: $"@model/{model} ",
                Description: "AI Model",
                Category: "Models",
                Priority: 0,
                Kind: AutocompleteKind.Other
            ));

        return Task.FromResult(items);
    }
}
