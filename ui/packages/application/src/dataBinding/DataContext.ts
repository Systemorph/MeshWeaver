import { toPath } from "lodash";
import { isBinding } from "./resolveBinding";

export type DataContextChangeHandler = (event: DataContextChangedEvent) => void;

export type DataContextChangedEvent = {
    value: unknown;
    object: object;
    key: string;
}

export class DataContext {
    readonly value: unknown;
    readonly parentContext: DataContext;
    readonly onChange: DataContextChangeHandler;

    resolveBinding(path: string) {
        const keys = toPath(path);
        let key: string;
        let value = this.value;
        let context: DataContext = this;
        let object: object = undefined;
        let level = 0;

        while (keys.length > 0) {
            key = keys.shift();
            object = value as object;

            try {
                // going up the context tree if property is missing, and we're at the root level of context
                while (level === 0 && context.parentContext && (!object || !object.hasOwnProperty(key))) {
                    context = context.parentContext;
                    object = context.value as object;
                }

                const currentValue = (object as any)?.[key];

                if (isBinding(currentValue)) {
                    keys.unshift(...toPath(currentValue.path));
                    context = context.parentContext;
                    value = context.value;
                    level = 0;
                }
                else {
                    value = currentValue;
                    level++;
                }
            }
            catch(error) {
                value = undefined;
                break;
            }
        }

        return new BindingResult(value, context, object, key);
    }
}

export class BindingResult {
    readonly value: unknown;
    readonly dataContext: DataContext;
    readonly object: object;
    readonly key: string;

    constructor(value: unknown, dataContext: DataContext, object: object, key: string) {
        this.value = value;
        this.dataContext = dataContext;
        this.object = object;
        this.key = key;
    }

    set(value: unknown) {
        const {object, key} = this;

        this.dataContext.onChange?.({
            value,
            object,
            key
        });
    }
}