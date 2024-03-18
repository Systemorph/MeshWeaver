import { contractMessage } from "../../../utils/src/contractMessage";

@contractMessage("OpenSmc.Application.ScopePropertyChanged")
export class ScopePropertyChanged {
    public readonly status: ScopeChangedStatus = 'Requested';
    public readonly errorMessage: string;

    constructor(public readonly scopeId: string,
                public readonly property: string,
                public readonly value: any) {
    }
}

type ScopeChangedStatus = 'Requested' | 'Committed' | 'NotFound' | 'Exception';