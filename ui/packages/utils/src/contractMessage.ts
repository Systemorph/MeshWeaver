import { last } from "lodash-es";

type Constructor<T = any> = { new (...args: any[]): T };

const typeRegistry = new Map<string, Constructor>();

export const getConstructor = (type: string) => {
    return typeRegistry.get(type);
};

// TODO: this needs a proper mixin support for decorators, see issue below (11/12/2021, akravets)
// https://github.com/microsoft/TypeScript/issues/4881
export function contractMessage(namespace: string) {
    return function<T extends Constructor>(constructor: T) {
        // TODO: backward compatibility, to be removed once namespaces are correct (3/26/2024, akravets)
        namespace = typeToNamespace(namespace, constructor.name);
        const $type = `${namespace}.${constructor.name}`;
        typeRegistry.set($type, constructor);
        return constructor;
    }
}

function typeToNamespace(namespace: string, name: string) {
    const parts = namespace.split('.');

    if (last(parts) === name) {
        parts.pop();
        return parts.join('.');
    }

    return namespace;
}

export function isMessageOfType<T>(message: any, constructor: ClassType<T>) {
    return getMessageType(message) === getMessageTypeConstructor(constructor);
}

export function getMessageTypeConstructor(constructor: ClassType<any>) {
    const { $type } = constructor as any;

    if (!$type)
        throw '$type is missing in constructor';

    return $type;
}

export function getMessageType(message: any) {
    const {$type} = message;

    if (!$type)
        throw '$type is missing in message';

    return $type;
}

export type ClassType<T> = new (...args: any[]) => T;