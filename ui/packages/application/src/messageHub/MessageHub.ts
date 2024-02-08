import { Observable, Observer } from "rxjs";
import { MessageDelivery } from "./MessageDelivery";

export type MessageHub = Observable<MessageDelivery> & Observer<MessageDelivery>;