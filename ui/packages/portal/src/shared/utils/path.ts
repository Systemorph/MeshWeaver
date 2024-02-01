const separator = '/';

export namespace Path {
    export function split(path: string) {
        return path.split(separator);
    }

    export function join(...parts: string[]) {
        return parts.join(separator);
    }

    export function dirname(path: string) {
        return split(path).slice(0, -1).join(separator);
    }

    export function basename(path: string) {
        return path.split(separator).pop();
    }

    export function extname(path: string) {
        const name = basename(path);
        return name.slice((Math.max(0, name.lastIndexOf('.')) || Infinity));
    }
}
