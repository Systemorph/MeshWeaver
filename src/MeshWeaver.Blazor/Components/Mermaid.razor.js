export function initMermaid(mode, element, diagram) {
    return ensureMermaidLoaded()
        .then(() => {
            // Determine theme based on system preference if needed
            let theme = 'default';
            if (mode === 2) {
                theme = 'dark';
            } else if (mode === 0) {
                // Check system preference
                if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
                    theme = 'dark';
                }
            }

            window.mermaid.initialize({
                theme: theme,
                startOnLoad: false,
                securityLevel: 'loose'
            });
        });
}

export function renderMermaid(mode, element, diagram) {
    // First initialize with correct theme
    return initMermaid(mode, element, diagram)
        .then(async () => {
            try {
                // Clear previous content
                element.innerHTML = '';

                // Create a wrapper with the diagram content
                const pre = document.createElement('pre');
                pre.className = 'mermaid';
                pre.textContent = diagram;
                element.appendChild(pre);

                // Render the diagram
                await window.mermaid.run({
                    nodes: [pre]
                });

                return true;
            } catch (error) {
                console.error("Mermaid rendering error:", error);
                element.innerHTML = `<div class="alert alert-danger">Error rendering diagram: ${error.message}</div>`;
                return false;
            }
        });
}

function ensureMermaidLoaded() {
    return new Promise((resolve, reject) => {
        if (window.mermaid) {
            resolve();
            return;
        }

        // Programmatically load the script
        const script = document.createElement('script');
        script.src = 'https://unpkg.com/mermaid/dist/mermaid.min.js'; // Alternative CDN
        script.onload = () => {
            console.log("Mermaid script loaded successfully");
            setTimeout(resolve, 100); // Small delay to ensure initialization
        };
        script.onerror = (e) => {
            console.error("Failed to load Mermaid library", e);
            reject(new Error("Mermaid library failed to load"));
        };
        document.head.appendChild(script);
    });
}