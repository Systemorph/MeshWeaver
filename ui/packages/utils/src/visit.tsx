import { isArray, isObjectLike } from "lodash";

export type PropertyPath = (string | number)[];

type QueueItem = [any, PropertyPath];

export function visit<T extends object>(root: T, iteratee: (node: unknown, path: PropertyPath) => void) {
    const visited: any[] = [];

    const queue: QueueItem[] = [
        [root, []]
    ];

    let current = queue.shift();

    while (current) {
        const [node, path] = current;

        if (!visited.includes(node)) {
            visited.push(node);

            if (!isArray(node)) {
                iteratee(node, path);
            }

            for (let k in node) {
                const next = node[k];

                if (isObjectLike(next)) {
                    queue.push([next, [...path, k]]);
                }
            }
        }

        current = queue.shift();
    }
}