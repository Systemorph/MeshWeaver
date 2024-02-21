import { contractMessage } from "./contractMessage";
import { v4 as uuid } from "uuid";
import { ControlDef } from "../ControlDef";
import {Style} from "../Style";

// TODO: fix namespace (2/21/2024, akravets)
@contractMessage("OpenSmc.Portal.LayoutAddress")
export class LayoutAddress {
    constructor(public id: string) {
    }
}

// TODO: fix namespace (2/21/2024, akravets)
@contractMessage("OpenSmc.Portal.UiAddress")
export class UiAddress {
    constructor(public id: string) {
    }
}

@contractMessage("OpenSmc.Layout.RefreshRequest")
export class RefreshRequest {
    constructor(public area = "") {
    }
}

@contractMessage("OpenSmc.Layout.AreaChangedEvent")
export class AreaChangedEvent {
    constructor(public area: string, public view?: ControlDef, public options?: any, public style?: Style) {
    }
}

@contractMessage("OpenSmc.Layout.Views.ClickedEvent")
export class ClickedEvent {
    constructor(public id?: string, public payload?: unknown) {
    }
}

@contractMessage("OpenSmc.Layout.Views.ExpandRequest")
export class ExpandRequest {
    constructor(public id: string, public area: string, public payload?: unknown) {

    }
}

@contractMessage("OpenSmc.Layout.SetAreaRequest")
export class SetAreaRequest {
    constructor(public area: string,
                public path: string,
                public options?: unknown) {
    }
}

@contractMessage("OpenSmc.Layout.CloseModalDialogEvent")
export class CloseModalDialogEvent {
}

// deprecated? ----------------------------------------
export type SelectionByCategory = Record<string, Named[]>;

@contractMessage("OpenSmc.Categories.CategoryItemsRequest")
export class CategoryItemsRequest {
    constructor(public readonly categoryName: string,
                public readonly search: string,
                public readonly page: number,
                public readonly pageSize: number,
    ) {
    }
}

@contractMessage("OpenSmc.Categories.CategoryItemsResponse")
export class CategoryItemsResponse {
    constructor(public readonly result: Named[],
                public readonly errorMessage?: string
    ) {
    }
}

// sent on category change
@contractMessage("OpenSmc.Categories.SetSelectionRequest")
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
        this.eventId = uuid();
    }
}

@contractMessage("OpenSmc.ErrorEvent")
export class ErrorEvent<T = unknown> {
    constructor(public readonly sourceEvent: T,
                public readonly message: string) {

    }
}

@contractMessage("OpenSmc.ModuleErrorEvent")
export class ModuleErrorEvent {
    public readonly sourceEvent: unknown;
    public readonly errorMessage: string;
    public readonly errorCode: string;
}
