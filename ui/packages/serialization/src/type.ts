import {immerable} from "immer"

type Constructor<T = unknown> = {
    new (...args: any): T;
    deserialize?(props: T): T;
}

const typeRegistry = new Map<string, Constructor>();

export const getConstructor = (type: string) => typeRegistry.get(type);

export function type(typeName: string) {
    return function<T extends Constructor>(constructor: T) {
        typeRegistry.set(typeName, constructor);
        (constructor as any).$type = typeName;
        (constructor as any)[immerable] = true;
        return constructor;
    }
}