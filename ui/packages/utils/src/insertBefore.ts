export function insertBefore<T>(array: T[], element: T, beforeElement?: T) {
    const index = array.indexOf(beforeElement);
    const beforeIndex = index === -1 ? array.length : index;
    return [...array.slice(0, beforeIndex), element, ...array.slice(beforeIndex)];
}