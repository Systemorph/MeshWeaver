const bindingType = "OpenSmc.Layout.DataBinding.Binding";

export type Binding = {
    $type: string;
    path: string;
}

export type Bindable<T> = T | Binding;

export function isBinding(data: Binding | unknown): data is Binding {
    return (data as Binding)?.$type === bindingType;
}

export function makeBinding(path: string) {
    return {
        $type: bindingType,
        path
    } as any
}