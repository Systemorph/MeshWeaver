import { Named } from "@open-smc/application/application.contract";

export type CategoryItemsRequestHandler = (category: string, callback: (items: Named[]) => void) => void;