using System.Text.Json;

namespace Memex.Client.Pages;

/// <summary>
/// Monaco editor hosted in a native MAUI <see cref="WebView"/> — the same editor the web portal uses, but
/// injected as plain JS/HTML (NOT the Blazor component, so it builds for MacCatalyst — no
/// <c>Microsoft.AspNetCore.App</c>/<c>NETSDK1082</c>). Native drives it via <c>EvaluateJavaScriptAsync</c>
/// (get/set text); model/agent pickers stay NATIVE popups layered beside it (see the chat composer).
///
/// <para>Monaco loads from the jsDelivr CDN for now (needs network on first paint); a follow-up can bundle
/// the <c>min/vs</c> assets under <c>Resources/Raw</c> and point the loader at them for fully-offline use.</para>
/// </summary>
public sealed class MonacoEditorView : ContentView
{
    private readonly WebView _web;

    public MonacoEditorView(string language = "markdown", string placeholder = "")
    {
        _web = new WebView
        {
            Source = new HtmlWebViewSource { Html = Html(language, placeholder) },
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
        };
        Content = _web;
    }

    /// <summary>Current editor text (empty until Monaco has finished loading).</summary>
    public async Task<string> GetTextAsync()
    {
        var raw = await _web.EvaluateJavaScriptAsync("window.mwGetText ? window.mwGetText() : ''");
        return raw ?? "";
    }

    /// <summary>Replaces the editor text.</summary>
    public void SetText(string text) =>
        _web.Eval($"window.mwSetText && window.mwSetText({JsonSerializer.Serialize(text)})");

    /// <summary>Inserts a snippet at the cursor (e.g. an "@agent" reference chosen from a native popup).</summary>
    public void Insert(string snippet) =>
        _web.Eval($"window.mwInsert && window.mwInsert({JsonSerializer.Serialize(snippet)})");

    // Plain raw string (no $-interpolation, so the JS braces stay literal); the two tokens are replaced.
    private static string Html(string language, string placeholder) => RawHtml
        .Replace("__LANG__", language)
        .Replace("__PLACEHOLDER__", placeholder.Replace("'", ""));

    private const string RawHtml = """
        <!DOCTYPE html>
        <html><head><meta name="viewport" content="width=device-width, initial-scale=1">
        <style>html,body,#editor{height:100%;width:100%;margin:0;padding:0}</style>
        <script src="https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs/loader.js"></script>
        </head><body>
        <div id="editor"></div>
        <script>
          require.config({ paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs' }});
          require(['vs/editor/editor.main'], function () {
            window.mwEditor = monaco.editor.create(document.getElementById('editor'), {
              value: '', language: '__LANG__', theme: 'vs-dark',
              minimap: { enabled: false }, lineNumbers: 'off', wordWrap: 'on',
              scrollBeyondLastLine: false, automaticLayout: true,
              placeholder: '__PLACEHOLDER__'
            });
            window.mwGetText = function () { return window.mwEditor.getValue(); };
            window.mwSetText = function (t) { window.mwEditor.setValue(t); };
            window.mwInsert  = function (s) {
              var ed = window.mwEditor, sel = ed.getSelection();
              ed.executeEdits('mw', [{ range: sel, text: s, forceMoveMarkers: true }]);
              ed.focus();
            };
          });
        </script></body></html>
        """;
}
