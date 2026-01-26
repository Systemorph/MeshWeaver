/**
 * Highlights code within the provided element
 * @param {HTMLElement} element - The element containing code to highlight
 */

// Load highlight.js if it's not already available
async function ensureHighlightJs() {
    if (window.hljs) return Promise.resolve();

    return new Promise((resolve) => {
        const script = document.createElement('script');
        script.src = 'https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/highlight.min.js';
        script.onload = () => resolve();
        document.head.appendChild(script);
    });
}

// Update the highlight.js theme based on current theme
function updateHighlightTheme() {
    let themeLink = document.getElementById('highlight-theme');

    // Create the theme link if it doesn't exist
    if (!themeLink) {
        themeLink = document.createElement('link');
        themeLink.id = 'highlight-theme';
        themeLink.rel = 'stylesheet';
        document.head.appendChild(themeLink);
    }

    // Determine if we're in dark mode
    let isDarkMode = false;

    // Check theme handler first
    if (window.themeHandler && typeof window.themeHandler.getEffectiveTheme === 'function') {
        isDarkMode = window.themeHandler.getEffectiveTheme() === 'dark';
    } else if (window.matchMedia) {
        // Fallback to system preference if theme handler not available
        isDarkMode = window.matchMedia('(prefers-color-scheme: dark)').matches;
    }

    // Update the theme stylesheet
    const newTheme = isDarkMode
        ? 'https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/styles/github-dark.min.css'
        : 'https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/styles/github.min.css';

    if (themeLink.href !== newTheme) {
        themeLink.href = newTheme;
    }
}

// Initialize theme update
function initializeThemeUpdates() {
    // Update theme initially
    updateHighlightTheme();

    // Register for theme changes if theme handler is available
    if (window.themeHandler && typeof window.themeHandler.registerThemeChangeCallback === 'function') {
        window.themeHandler.registerThemeChangeCallback(() => {
            updateHighlightTheme();
        });
    }

    // Also listen for system theme changes as fallback
    if (window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
            updateHighlightTheme();
        });
    }
}

export async function highlightBlock(element) {
    // Check if element is valid
    if (!element) {
        return;
    }

    // Ensure hljs is loaded
    await ensureHighlightJs();

    // Initialize theme updates (only once)
    if (!window.highlightThemeInitialized) {
        initializeThemeUpdates();
        window.highlightThemeInitialized = true;
    }

    // Find all code blocks within the element
    const codeElements = element.querySelectorAll("pre code");

    if (codeElements.length === 0) {
        // If no pre code elements found, try to highlight the element itself if it's a code element
        if (element.tagName === 'CODE' || element.classList.contains('language-')) {
            hljs.highlightElement(element);
        } else {
            // Look for any code elements
            const allCodeElements = element.querySelectorAll("code");
            allCodeElements.forEach(block => {
                hljs.highlightElement(block);
            });
        }
    } else {
        codeElements.forEach(block => {
            hljs.highlightElement(block);
        });
    }
}

export function getCodeText(element) {
    if (!element) {
        return "";
    }

    const codeElement = element.querySelector("pre code") || element.querySelector("code");
    return codeElement ? codeElement.textContent : element.textContent || "";
}