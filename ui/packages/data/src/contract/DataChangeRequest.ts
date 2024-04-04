import { Request } from '@open-smc/messaging/src/api/Request';
import { DataChangeResponse } from "./DataChangeResponse";

export class DataChangeRequest extends Request<DataChangeResponse> {
    constructor() {
        super(DataChangeResponse);
    }
}