import { contractMessage } from "./contractMessage";
import { v4 as uuid } from "uuid";
import { ControlDef } from "../ControlDef";
import {Style} from "../Style";

@contractMessage("OpenSmc.Application.Layout.LayoutAddress")
export class LayoutAddress {
    constructor(public id: string) {
    }
}

// @contractMessage("OpenSmc.Application.ApplicationAddress")
// export class ApplicationAddress {
//     constructor(public project: string, public id: string) {
//     }
// }

@contractMessage("OpenSmc.Messaging.ConnectToHubRequest")
export class ConnectToHubRequest {
    constructor(public from: any, public to: any) {
    }
}

@contractMessage("OpenSmc.Application.UiAddress")
export class UiAddress {
    constructor(public id: string) {
    }
}

// @contractMessage("OpenSmc.Application.MainLayoutAddress")
// export class MainLayoutAddress {
//     applicationAddress: object;
//
//     constructor(public id: string, public host: object) {
//         this.applicationAddress = host;
//     }
// }

@contractMessage("OpenSmc.Application.RefreshRequest")
export class RefreshRequest {
    constructor(public area: string) {
    }
}

@contractMessage("OpenSmc.Application.AreaChangedEvent")
export class AreaChangedEvent<TOptions = unknown> {
    constructor(public area: string, public view?: ControlDef, public options?: TOptions, public style?: Style) {
    }
}

@contractMessage("OpenSmc.Application.Layout.Views.ClickedEvent")
export class ClickedEvent {
    constructor(public id?: string, public payload?: unknown) {
    }
}

@contractMessage("OpenSmc.Application.Layout.Views.ExpandRequest")
export class ExpandRequest {
    constructor(public id: string, public area: string, public payload?: unknown) {

    }
}

export interface AreaDependency {
    readonly scopeId: string;
    readonly property: string;
}

@contractMessage("OpenSmc.Application.SetAreaRequest")
export class SetAreaRequest {
    constructor(public area: string,
                public path: string,
                public options?: unknown) {
    }
}

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

@contractMessage("OpenSmc.Application.CloseModalDialogEvent")
export class CloseModalDialogEvent {
}

export class Dispose {

}
