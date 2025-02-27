import mermaid from 'mermaid';

export function renderMermaid(isDark: Boolean, element: HTMLElement, diagram: string) {
    if (isDark) {
        mermaid.initialize({ theme: 'dark' });
    } else {
        mermaid.initialize({ theme: 'default' });
    }

    try {
        if (element) {
            element.innerHTML = diagram;
            element.removeAttribute('data-processed');
            mermaid.contentLoaded();
        }
    } catch (error) {
        console.warn('Error initializing mermaid diagrams:', error);
    }
}