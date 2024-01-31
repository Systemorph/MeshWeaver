export function insertAfter<T>(array: T[], element: T, afterElement?: T) {
    const index = array.indexOf(afterElement);
    const afterIndex = index === -1 ? 0 : index + 1;
    return [...array.slice(0, afterIndex), element, ...array.slice(afterIndex)];
}