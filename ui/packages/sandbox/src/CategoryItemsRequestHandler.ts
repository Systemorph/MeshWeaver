import { Named } from "@open-smc/application/src/application.contract";

export type CategoryItemsRequestHandler = (category: string, callback: (items: Named[]) => void) => void;