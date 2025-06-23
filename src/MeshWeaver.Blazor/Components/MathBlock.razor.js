/**
 * Load MathJax (if needed) and typeset the provided element
 * @param {HTMLElement} element - The element containing MathJax content
 * @returns {Promise<boolean>} - A promise that resolves when typesetting is complete
 */
export async function loadAndTypeset(element) {
    console.log("Starting MathJax loading and typesetting process");

    // Check if MathJax is already loaded and initialized
    if (!window.MathJax || !window.MathJax.typesetPromise) {
        // Configure MathJax before loading
        window.MathJax = {
            tex: {
                inlineMath: [['$', '$'], ['\\(', '\\)']],
                displayMath: [['$$', '$$'], ['\\[', '\\]']],
                packages: ['base', 'ams', 'noundefined', 'newcommand', 'boldsymbol']
            },
            startup: {
                typeset: false
            }
        };

        // Create script element
        const script = document.createElement('script');
        script.src = 'https://cdn.jsdelivr.net/npm/mathjax@3/es5/tex-svg.js';
        script.id = 'mathjax-script';

        // Wait for script to load
        const loaded = await new Promise(resolve => {
            script.onload = () => resolve(true);
            script.onerror = () => {
                console.error("Failed to load MathJax");
                resolve(false);
            };
            document.head.appendChild(script);
        });

        if (!loaded) return false;

        // Wait for MathJax to be ready
        await new Promise(resolve => {
            function checkMathJax() {
                if (window.MathJax && window.MathJax.typesetPromise) {
                    resolve();
                } else {
                    setTimeout(checkMathJax, 100);
                }
            }
            checkMathJax();
        });
    }

    // Now typeset the element
    try {
        if (element && window.MathJax && window.MathJax.typesetPromise) {
            console.log("Typesetting element");
            await window.MathJax.typesetPromise([element]);
            return true;
        } else {
            console.warn("Cannot typeset: element or MathJax not ready");
            return false;
        }
    } catch (error) {
        console.error('Error typesetting MathJax elements:', error);
        return false;
    }
}