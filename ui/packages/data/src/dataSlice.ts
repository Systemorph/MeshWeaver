import type { PayloadAction } from "@reduxjs/toolkit";
import { createSlice } from "@reduxjs/toolkit";
import { WorkspaceReference } from "./data.contract";
import { JSONPath } from "jsonpath-plus";

export interface DataSliceState {
    workspace?: any;
}

const initialState: DataSliceState = {

}
export const dataSlice = createSlice({
    name: "data",
    initialState,
    reducers: create => ({
        setWorkspace: create.reducer(
            (state, action: PayloadAction<any>) => {
                state.workspace = action.payload;
            },
        ),
    })
});

export const {setWorkspace} = dataSlice.actions;

export const selectByWorkspaceReference = (state: DataSliceState, reference: WorkspaceReference) =>
    JSONPath({path: reference.path, json: state.workspace});