import { PathReferenceBase } from "./PathReferenceBase";

export type ValueOrReference<T> = PathReferenceBase<T> | {
    [TKey in keyof T]: ValueOrReference<T[TKey]>;
}