import { Observer, Subscribable } from "rxjs";
import { MessageDelivery } from "@open-smc/application/SignalrHub";

export type MessageHub = Subscribable<MessageDelivery> & Observer<MessageDelivery>;