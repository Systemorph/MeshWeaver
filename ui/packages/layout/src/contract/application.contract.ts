import { type } from "@open-smc/serialization/src/type";

// TODO: fix namespace (2/21/2024, akravets)
@type("OpenSmc.Portal.LayoutAddress")
export class LayoutAddress {
    constructor(public id: string) {
    }
}

// TODO: fix namespace (2/21/2024, akravets)
@type("OpenSmc.Portal.UiAddress")
export class UiAddress {
    constructor(public id: string) {
    }
}

@type("OpenSmc.Layout.Views.ClickedEvent")
export class ClickedEvent {
    constructor(public payload?: unknown) {
    }
}

@type("OpenSmc.Layout.Views.ExpandRequest")
export class ExpandRequest {
    constructor(public id: string, public area: string, public payload?: unknown) {

    }
}

@type("OpenSmc.Layout.CloseModalDialogEvent")
export class CloseModalDialogEvent {
}

// deprecated ----------------------------------------
export type SelectionByCategory = Record<string, Named[]>;

@type("OpenSmc.Categories.CategoryItemsRequest")
export class CategoryItemsRequest {
    constructor(public readonly categoryName: string,
                public readonly search: string,
                public readonly page: number,
                public readonly pageSize: number,
    ) {
    }
}

@type("OpenSmc.Categories.CategoryItemsResponse")
export class CategoryItemsResponse {
    constructor(public readonly result: Named[],
                public readonly errorMessage?: string
    ) {
    }
}

// sent on category change
@type("OpenSmc.Categories.SetSelectionRequest")
export class SetSelectionRequest {
    constructor(public readonly selection: Record<string, string[]>) {
    }
}

export interface Category {
    readonly category: string;
    readonly displayName: string;
    readonly type: 'Complete' | 'Searchable';
}

export interface Named {
    systemName: string;
    displayName: string;
}

// TODO: legacy (8/7/2023, akravets)

export type EventStatus = 'Requested' | 'Committed' | 'Rejected' | 'AccessDenied' | 'NotFound' | 'Ignored' | 'InvalidSubscription';

export class BaseEvent {
    public eventId: string;
    public status: EventStatus = 'Requested';

    constructor() {
        // this.eventId = uuid();
    }
}

@type("OpenSmc.ErrorEvent")
export class ErrorEvent<T = unknown> {
    constructor(public readonly sourceEvent: T,
                public readonly message: string) {

    }
}