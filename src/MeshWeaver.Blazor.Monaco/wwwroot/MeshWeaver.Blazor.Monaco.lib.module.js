// JavaScript initializer for MeshWeaver.Blazor.Monaco
// This file is automatically loaded by Blazor when the library is referenced.
// It dynamically loads the BlazorMonaco scripts so they don't need to be
// manually added to App.razor.

let scriptsLoaded = false;
let scriptsLoading = null;

function loadScript(src) {
    return new Promise((resolve, reject) => {
        // Check if already loaded
        if (document.querySelector(`script[src="${src}"]`)) {
            resolve();
            return;
        }

        const script = document.createElement('script');
        script.src = src;
        script.onload = () => resolve();
        script.onerror = (e) => reject(new Error(`Failed to load script: ${src}`));
        document.head.appendChild(script);
    });
}

async function loadMonacoScripts() {
    if (scriptsLoaded) {
        return;
    }

    if (scriptsLoading) {
        return scriptsLoading;
    }

    scriptsLoading = (async () => {
        try {
            // Load scripts in order - loader must come before editor.main
            await loadScript('_content/BlazorMonaco/jsInterop.js');
            await loadScript('_content/BlazorMonaco/lib/monaco-editor/min/vs/loader.js');
            await loadScript('_content/BlazorMonaco/lib/monaco-editor/min/vs/editor/editor.main.js');

            scriptsLoaded = true;
            console.log('MeshWeaver.Blazor.Monaco: BlazorMonaco scripts loaded successfully');
        } catch (error) {
            console.error('MeshWeaver.Blazor.Monaco: Failed to load BlazorMonaco scripts', error);
            throw error;
        }
    })();

    return scriptsLoading;
}

// Blazor JavaScript initializer - called BEFORE Blazor starts
// This ensures Monaco scripts are loaded before any components try to use them
export async function beforeWebStart() {
    await loadMonacoScripts();
}

// Also support beforeStart for other Blazor hosting models
export async function beforeStart() {
    await loadMonacoScripts();
}

// Fallback for afterWebStarted (in case beforeWebStart isn't called)
export function afterWebStarted(blazor) {
    if (!scriptsLoaded) {
        loadMonacoScripts();
    }
}

// Also export for manual loading if needed
export { loadMonacoScripts };
