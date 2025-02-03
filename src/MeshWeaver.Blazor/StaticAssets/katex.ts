import katex from 'katex';
import 'katex/dist/katex.min.css';

export function renderElement(element: HTMLElement) {
    const elements = element.querySelectorAll('.math');
    elements.forEach(el => {
        const math = el.textContent?.replace(/^\s*\\\[\s*|\s*\\\]\s*$/g, '') || '';
        katex.render(math, el as HTMLElement, {
            throwOnError: true,
            displayMode: true,
            output: 'mathml'
        });
    });
}