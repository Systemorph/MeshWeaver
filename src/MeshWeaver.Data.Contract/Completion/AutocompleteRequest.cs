#nullable enable

using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Request for autocomplete suggestions based on query and context.
/// </summary>
/// <param name="Query">The search query (text being typed, including any @ or / prefix).</param>
/// <param name="Context">The current unified reference context (e.g., "pricing/MS-2024").</param>
public record AutocompleteRequest(string Query, string? Context) : IRequest<AutocompleteResponse>;
