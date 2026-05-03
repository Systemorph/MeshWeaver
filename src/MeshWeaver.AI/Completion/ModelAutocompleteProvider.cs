#nullable enable

using System.Reactive.Linq;
using MeshWeaver.Data.Completion;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Provides autocomplete items for AI models.
/// Gets models from IChatClientFactory when available.
/// </summary>
public class ModelAutocompleteProvider : IAutocompleteProvider
{
    private readonly IChatClientFactory? _chatClientFactory;
    private IReadOnlyList<string>? _availableModels;

    public ModelAutocompleteProvider(IChatClientFactory chatClientFactory)
    {
        _chatClientFactory = chatClientFactory;
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
    public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null)
    {
        // No external I/O — pure in-memory enumeration of the model list.
        IReadOnlyList<string>? models =
            _chatClientFactory?.Models ?? _availableModels;
        if (models is null)
            return Observable.Empty<AutocompleteItem>();

        return models
            .Select(model => new AutocompleteItem(
                Label: $"@model/{model}",
                InsertText: $"@model/{model} ",
                Description: "AI Model",
                Category: "Models",
                Priority: 0,
                Kind: AutocompleteKind.Other))
            .ToObservable();
    }
}
