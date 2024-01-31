import { cloneDeepWith, isObject } from "lodash";
import { isBinding } from "./resolveBinding";
import { DataContext } from "./DataContext";

export const bindIteratee = (value: any) => isObject(value) ? undefined : value;

export type BindIteratee = typeof bindIteratee;

// TODO: replace iteratee with filter (10/27/2023, akravets)
export function bind<T>(value: T, dataContext: DataContext, iteratee = bindIteratee) {
    return cloneDeepWith(
        value,
        value => {
            return isBinding(value)
                ? dataContext.resolveBinding(value.path)?.value ?? null
                : iteratee(value);
        }
    );
}

// TODO: sample (10/26/2023, akravets)

const parent = {
    scopes: [
        {
            $scopeId: "123",
            name: "B",
            test: makeBinding("test")
        }
    ]
}

const dataContext = {
    name: "A",
    scope: makeBinding("scopes[0]")
}

const view = {
    data: makeBinding("scope.name")
}

function makeBinding(path: string) {
    return path;
}