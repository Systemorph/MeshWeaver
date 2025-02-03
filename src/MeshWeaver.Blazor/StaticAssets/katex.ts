import katex from 'katex';
import 'katex/dist/katex.min.css';

export function renderElement(element: HTMLElement) {
    const elements = element.querySelectorAll('.math');
    elements.forEach(el => {
        const math = el.textContent || '';
        katex.render(math, el as HTMLElement, {
            throwOnError: true
        });
    });
}