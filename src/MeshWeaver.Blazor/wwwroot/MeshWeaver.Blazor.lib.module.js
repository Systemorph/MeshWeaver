// JavaScript initializer for MeshWeaver.Blazor
// This file is automatically loaded by Blazor when the library is referenced.
// BlazorMonaco scripts (loader.js, editor.main.js, jsInterop.js) must be
// included as <script> tags in App.razor before blazor.web.js so that
// Monaco is available synchronously when editor components render.

// Blazor JavaScript initializer - called automatically by Blazor framework
export function afterWebStarted(blazor) {
    // Register global helper for file downloads from DotNetStreamReference
    window.meshweaverDownloadFileFromStream = async (fileName, streamRef) => {
        const arrayBuffer = await streamRef.arrayBuffer();
        const blob = new Blob([arrayBuffer], { type: 'application/octet-stream' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    };
}
