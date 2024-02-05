import { CancelTokenSource } from "axios";
import { PropsWithChildren, useEffect, useMemo } from "react";
import { clearFileSelectorsCache } from "./hooks/useUploadFormFile";
import { createStoreContext } from "@open-smc/store/src/storeContext";

export type UploadFormState = {
    readonly files: Record<string, UploadFileModel>;
    readonly fileIds: string[];
}

export type UploadFileModel = {
    readonly id: string;
    readonly blob: File;
    readonly conflict?: boolean;
    readonly conflictResolution?: ConflictResolution;
    readonly newName?: string;
    readonly status?: 'New' | 'InProgress' | 'Complete' | 'Canceled' | 'Error';
    readonly progressEvent?: ProgressEvent;
    readonly cancelTokenSource?: CancelTokenSource;
}

export type ConflictResolution = 'Overwrite' | 'Rename';

export const {useStore, useSelector, StoreProvider} = createStoreContext<UploadFormState>();

export function UploadFormStore({children}: PropsWithChildren<{}>) {
    const initialState = useMemo(() => ({
        files: {},
        fileIds: []
    }), []);

    useEffect(() => () => clearFileSelectorsCache(), []);

    return <StoreProvider initialState={initialState}>{children}</StoreProvider>;
}
