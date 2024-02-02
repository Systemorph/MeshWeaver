import { Observer, Subscribable } from "rxjs";
import { MessageDelivery } from "@open-smc/application/src/SignalrHub";

export type MessageHub = Subscribable<MessageDelivery> & Observer<MessageDelivery>;