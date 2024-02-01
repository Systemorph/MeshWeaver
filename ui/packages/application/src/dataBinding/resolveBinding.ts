const bindingType = "OpenSmc.Application.Layout.DataBinding.Binding";

export interface Binding {
    $type: string;
    path: string;
}

export function isBinding(data: Binding | unknown): data is Binding {
    return (data as Binding)?.$type === bindingType;
}

export function makeBinding(path: string) {
    return {
        $type: bindingType,
        path
    } as any
}