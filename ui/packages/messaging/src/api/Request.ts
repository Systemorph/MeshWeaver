export class Request<T> {
    constructor(public responseType: abstract new (...args: any) => T) {
    }
}