#nullable enable

using MeshWeaver.Data.Completion;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Provides autocomplete items for AI models.
/// Gets models from IAgentChatFactoryProvider when available.
/// </summary>
public class ModelAutocompleteProvider : IAutocompleteProvider
{
    private readonly IAgentChatFactoryProvider? _factoryProvider;
    private IReadOnlyList<string>? _availableModels;

    public ModelAutocompleteProvider(IAgentChatFactoryProvider factoryProvider)
    {
        _factoryProvider = factoryProvider;
    }

    public ModelAutocompleteProvider()
    {
    }

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
        IReadOnlyList<string> models;

        if (_factoryProvider != null)
        {
            models = _factoryProvider.AllModels;
        }
        else if (_availableModels != null)
        {
            models = _availableModels;
        }
        else
        {
            return Task.FromResult<IEnumerable<AutocompleteItem>>([]);
        }

        var items = models
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
