namespace MeshWeaver.Blazor.Components.Monaco;

/// <summary>
/// One code-completion request from the Monaco editor: the FULL in-flight buffer text and the
/// caret position, already converted to the LSP convention (0-based line and character — Monaco
/// is 1-based; <c>MonacoEditorView.GetCodeCompletions</c> converts at the boundary). Distinct
/// from the trigger-token completion path (<c>CompletionCallback</c>), which matches a typed
/// <c>@</c>-reference query and knows nothing about positions.
/// </summary>
public readonly record struct CodeCompletionRequest(string Text, int Line, int Character);
