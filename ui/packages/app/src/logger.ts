import { MessageDelivery } from "@open-smc/message-hub/src/api/MessageDelivery";

export const down = ["%c↓", `color: green`];
export const up = ["%c↑", `color: red`];

export const makeLogger = (icon: string[]) => (delivery: MessageDelivery) =>
    console.log(...icon, (delivery as any)?.message?.$type, delivery);