export class Request<T> {
    constructor(public readonly responseType: abstract new (...args: any) => T) {}
}