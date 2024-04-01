type Constructor<T = any> = new (...args: any) => T;

const typeRegistry = new Map<string, Constructor>();

export const getConstructor = (type: string) => typeRegistry.get(type);

export function type(typeName: string) {
    return function(constructor: Constructor) {
        typeRegistry.set(typeName, constructor);
        (constructor as any).$type = typeName;
        return constructor;
    }
}