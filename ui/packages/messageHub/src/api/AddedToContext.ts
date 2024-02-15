import { MessageHub } from "./MessageHub";

export class AddedToContext {
    constructor(public context: MessageHub) {
    }
}