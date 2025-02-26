import mermaid from 'mermaid';

export function contentLoaded(isDark: Boolean, ids: string[]) {
    if (isDark) {
        mermaid.initialize({ theme: 'dark' });
    }

    try {
        for (const id of ids) {
            const element = document.getElementById(id);
            if (element) {
                mermaid.init(undefined, element);
            }
        }
    } catch (error) {
        console.warn('Error initializing mermaid diagrams:', error);
    }
}