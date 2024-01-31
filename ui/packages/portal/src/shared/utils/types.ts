export type Action<TArg> = (arg: TArg) => void;
export type Func<TArg1, TResult> = (arg1: TArg1) => TResult;
export type Predicate<TArg> = (arg: TArg) => boolean;
export type Factory<TResult> = () => TResult;