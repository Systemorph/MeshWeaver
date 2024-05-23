import { IMessageHub } from "../MessageHub";

export class AddedToContext {
    constructor(public context: IMessageHub) {
    }
}