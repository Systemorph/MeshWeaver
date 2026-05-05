#nullable enable

using System.Reactive.Linq;
using MeshWeaver.Data.Completion;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Provides autocomplete items for AI models.
/// Aggregates the union of <see cref="IChatClientFactory.Models"/> across every
/// registered factory so the dropdown reflects the actual installed factories
/// rather than a single one. Without this, the prod portal showed model names
/// (e.g. via SetAvailableModels from a stale source) that no factory could
/// actually serve, and selecting one ended up as "model not found in any
/// factory" at runtime.
/// </summary>
public class ModelAutocompleteProvider : IAutocompleteProvider
{
    private readonly IEnumerable<IChatClientFactory> _chatClientFactories;
    private IReadOnlyList<string>? _availableModels;

    public ModelAutocompleteProvider(IEnumerable<IChatClientFactory> chatClientFactories)
    {
        _chatClientFactories = chatClientFactories;
    }

    public ModelAutocompleteProvider()
    {
        _chatClientFactories = Array.Empty<IChatClientFactory>();
    }

    /// <summary>
    /// Sets the available models for autocomplete.
    /// Called when models are loaded or changed. Factory-sourced models always
    /// take precedence over a manual override — manual SetAvailableModels is
    /// only used when there are no factories registered (test / minimal hosts).
    /// </summary>
    public void SetAvailableModels(IReadOnlyList<string> models)
    {
        _availableModels = models;
    }

    /// <inheritdoc />
    public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null)
    {
        // No external I/O — pure in-memory enumeration of the model list.
        // Order: union across all factories (sorted by factory.Order, then model
        // name); fall back to manual list when no factories are registered.
        var factoryModels = _chatClientFactories
            .OrderBy(f => f.Order)
            .SelectMany(f => f.Models.Select(m => (Factory: f.Name, Model: m)))
            .GroupBy(p => p.Model)
            .Select(g => g.First())
            .ToList();

        if (factoryModels.Count > 0)
        {
            return factoryModels
                .Select(p => new AutocompleteItem(
                    Label: $"@model/{p.Model}",
                    InsertText: $"@model/{p.Model} ",
                    Description: $"AI Model ({p.Factory})",
                    Category: "Models",
                    Priority: 0,
                    Kind: AutocompleteKind.Other))
                .ToObservable();
        }

        if (_availableModels is null)
            return Observable.Empty<AutocompleteItem>();

        return _availableModels
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
