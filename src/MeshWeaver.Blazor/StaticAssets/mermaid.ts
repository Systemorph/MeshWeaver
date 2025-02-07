import mermaid from 'mermaid';

export function contentLoaded(isDark: Boolean) {
    if (isDark)
        mermaid.initialize({ theme: 'dark' });
    mermaid.contentLoaded();
}