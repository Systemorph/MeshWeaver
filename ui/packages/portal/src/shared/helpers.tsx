export function getFileName(path: string) {
    return path?.split('\\').pop().split('/').pop();
}

export function insert(array: any[], index: number, ...elements: any[]) {
    return array.slice(0, index).concat(elements, array.slice(index));
}