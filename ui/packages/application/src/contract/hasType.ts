export const hasType = (value: any): value is Typed => value?.$type !== undefined;

export type Typed = {
    $type: string;
}