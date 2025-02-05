declare const MathJax: any;

export function typeset() {
    if (typeof MathJax === 'undefined') {
        console.warn('MathJax not loaded');
        return Promise.resolve();
    }
    return MathJax.typesetPromise();
}