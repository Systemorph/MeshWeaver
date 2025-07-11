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
    console.log('updateHighlightTheme called');

    let themeLink = document.getElementById('highlight-theme');

    // Create the theme link if it doesn't exist
    if (!themeLink) {
        console.log('Creating highlight-theme link element');
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
        console.log('Theme from themeHandler:', window.themeHandler.getEffectiveTheme());
    } else if (window.matchMedia) {
        // Fallback to system preference if theme handler not available
        isDarkMode = window.matchMedia('(prefers-color-scheme: dark)').matches;
        console.log('Theme from matchMedia:', isDarkMode ? 'dark' : 'light');
    }

    // Update the theme stylesheet
    const newTheme = isDarkMode
        ? 'https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/styles/github-dark.min.css'
        : 'https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/styles/github.min.css';

    console.log('Setting theme to:', newTheme);

    if (themeLink.href !== newTheme) {
        themeLink.href = newTheme;
        console.log('Theme updated successfully');
    } else {
        console.log('Theme already set correctly');
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
    console.log('highlightBlock called with element:', element);

    // Check if element is valid
    if (!element) {
        console.warn('CodeBlock: Element reference is null');
        return;
    }

    // Ensure hljs is loaded
    await ensureHighlightJs();

    // Initialize theme updates (only once)
    if (!window.highlightThemeInitialized) {
        console.log('Initializing theme updates');
        initializeThemeUpdates();
        window.highlightThemeInitialized = true;
    }

    // Find all code blocks within the element
    const codeElements = element.querySelectorAll("pre code");
    console.log('Found code elements:', codeElements.length);

    if (codeElements.length === 0) {
        // If no pre code elements found, try to highlight the element itself if it's a code element
        if (element.tagName === 'CODE' || element.classList.contains('language-')) {
            console.log('Highlighting element itself');
            hljs.highlightElement(element);
        } else {
            // Look for any code elements
            const allCodeElements = element.querySelectorAll("code");
            console.log('Found alternative code elements:', allCodeElements.length);
            allCodeElements.forEach(block => {
                hljs.highlightElement(block);
            });
        }
    } else {
        console.log('Highlighting pre code elements');
        codeElements.forEach(block => {
            hljs.highlightElement(block);
        });
    }
}

export function getCodeText(element) {
    if (!element) {
        console.warn('CodeBlock: Element reference is null');
        return "";
    }

    const codeElement = element.querySelector("pre code") || element.querySelector("code");
    return codeElement ? codeElement.textContent : element.textContent || "";
}