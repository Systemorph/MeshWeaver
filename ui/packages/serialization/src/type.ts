type Constructor = { new (...args: any): {} };

const typeRegistry = new Map<string, Constructor>();

export const getConstructor = (type: string) => typeRegistry.get(type);

export function type(typeName: string) {
    return function<T extends Constructor>(constructor: T) {
        typeRegistry.set(typeName, constructor);
        (constructor as any).$type = typeName;
        return constructor;
    }
}