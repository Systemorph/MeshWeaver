export class Request<out T> {
    constructor(public responseType: abstract new (...args: any) => T) {
    }
}