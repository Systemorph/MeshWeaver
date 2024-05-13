import { IMessageHub } from "../MessageHub";

export class AddToContextRequest {
    constructor(public hub: IMessageHub, public address: any) {
    }
}