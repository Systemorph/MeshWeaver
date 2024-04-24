import { Observable, Observer } from "rxjs";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery.ts";
import { SampleApp } from "./app/SampleApp.ts";

export class SamplesServer extends Observable<MessageDelivery> implements Observer<MessageDelivery> {
    private app = new SampleApp();

    constructor() {
        super(
            subscriber =>
                this.app
                    .subscribe(subscriber)
        );
    }

    complete() {
    }

    error(err: any) {
    }

    next(value: MessageDelivery) {
        this.app.next(value);
    }
}