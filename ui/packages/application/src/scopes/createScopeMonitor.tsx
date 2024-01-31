import { getOrAdd } from "@open-smc/utils/getOrAdd";
import { produce } from "immer";
import { set } from "lodash";
import { visit, PropertyPath } from "@open-smc/utils/visit";

export function createScopeMonitor<T extends object>(data: T, next: (data: T) => void) {
    const scopes = new Map<string, PropertyPath[]>;

    visit(data, (node, path) => {
        if (isScope(node)) {
            const paths = getOrAdd(scopes, (node as Scope).$scopeId, () => []);
            paths.push(path);
        }
    });

    return function setScopeProperty(scopeId: string, property: string, value: unknown) {
        if (scopes.has(scopeId)) {
            const paths = scopes.get(scopeId);

            const nextData = produce(data, draft => {
                for (let path of paths) {
                    set(draft as object, path ? [...path, property] : property, value);
                }
            });

            next(nextData);
        }
    }
}

export type SetScopeProperty = (scopeId: string, property: string, value: unknown) => void;

export interface Scope {
    readonly $scopeId: string;
    readonly [key: string]: unknown;
}

export function isScope(obj: Scope | unknown): obj is Scope {
    return !!(obj as Scope)?.$scopeId;
}