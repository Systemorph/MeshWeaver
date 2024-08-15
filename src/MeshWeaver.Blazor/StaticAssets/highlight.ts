import hljs from 'highlight.js';

export function highlightCode(element: HTMLElement) {
    var preTagList = element.getElementsByTagName('pre');
    for (let el of preTagList) {
        var codeTag = el.getElementsByTagName('code');
        hljs.highlightElement(codeTag[0]);
    }
}