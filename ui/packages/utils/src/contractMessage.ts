type Constructor<T = any> = { new (...args: any[]): T };

// TODO: this needs a proper mixin support for decorators, see issue below (11/12/2021, akravets)
// https://github.com/microsoft/TypeScript/issues/4881
export function contractMessage(type: string) {
    return function<T extends Constructor>(constructor: T) {
        return class extends constructor {
            static $type = type;
            $type = type;
        }
    }
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