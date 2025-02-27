import mermaid from 'mermaid';

export function contentLoaded(isDark: Boolean, element: HTMLElement, html: string) {
    if (isDark) {
        mermaid.initialize({ theme: 'dark' });
    }

    try {
        if (element) {
            element.innerHTML = html;
            mermaid.init(undefined, element);
        }
    } catch (error) {
        console.warn('Error initializing mermaid diagrams:', error);
    }
}