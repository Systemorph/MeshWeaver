import { MessageHub } from "./MessageHub";

export class AddToContextRequest {
    constructor(public hub: MessageHub, public address: any) {
    }
}