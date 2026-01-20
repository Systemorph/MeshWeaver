#nullable enable

using System.Runtime.CompilerServices;
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
    public async IAsyncEnumerable<AutocompleteItem> GetItemsAsync(
        string query,
        string? contextPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        IReadOnlyList<string> models;

        if (_chatClientFactory != null)
        {
            models = _chatClientFactory.Models;
        }
        else if (_availableModels != null)
        {
            models = _availableModels;
        }
        else
        {
            yield break;
        }

        await Task.CompletedTask; // Satisfy async requirement

        foreach (var model in models)
        {
            yield return new AutocompleteItem(
                Label: $"@model/{model}",
                InsertText: $"@model/{model} ",
                Description: "AI Model",
                Category: "Models",
                Priority: 0,
                Kind: AutocompleteKind.Other
            );
        }
    }
}
