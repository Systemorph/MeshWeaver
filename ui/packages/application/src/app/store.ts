import { configureStore, createAction, createReducer } from "@reduxjs/toolkit"
import { dataSyncHub } from "./dataSyncHub";
import { Style } from "../Style";
import { createWorkspace } from "@open-smc/data/src/workspace";
import { subscribeToDataChanges } from "@open-smc/data/src/subscribeToDataChanges";
import { EntireWorkspace, LayoutAreaReference } from "@open-smc/data/src/data.contract";
import { MessageHub } from "@open-smc/message-hub/src/api/MessageHub";
import { distinctUntilChanged, distinctUntilKeyChanged, from, map } from "rxjs";
import { LayoutArea } from "../contract/application.contract";

export type RootState = {
    rootArea: string;
    areas: Record<string, LayoutAreaModel>;
}

export type LayoutAreaModel = {
    id: string;
    control: ControlModel;
    options?: any;
    style?: Style;
}

export type ControlModel = {
    componentTypeName: string;
    props: { [prop: string]: unknown };
}

export interface SetPropAction {
    areaId: string;
    prop: string;
    value: any;
}

export const setProp = createAction<SetPropAction>('setProp');

const rootReducer = createReducer<RootState>(
    null,
    builder => {
        builder.addCase(setProp, (state, action) => {
            const {areaId, prop, value} = action.payload;
            (state.areas[areaId].control.props as any)[prop] = value;
        })
    }
);

export const makeStore = (hub: MessageHub) => {
    const dataStore = createWorkspace();
    subscribeToDataChanges(hub, new EntireWorkspace(), dataStore.dispatch);

    const layoutStore = createWorkspace<LayoutArea>();
    subscribeToDataChanges(hub, new LayoutAreaReference("/"), layoutStore.dispatch);

    const store = configureStore({
        reducer: rootReducer
    });

    from(layoutStore).pipe(
        distinctUntilChanged((previous, current) => {
            return current?.id === previous?.id
                && current?.options === previous?.options
                && current?.style === previous?.style;
        })
    );

    // subscribe to data-bound props, on change update data context
    from(store)
        .pipe(map(state => state.areas["123"].control.props["data"]))
        .pipe(distinctUntilChanged())
        .subscribe(value => {
            // update dataContext
        });

    return store;
}

export const store = makeStore(dataSyncHub);

export type AppStore = typeof store;

export type AppDispatch = AppStore["dispatch"];

export const layoutAreaSelector = (id: string) => (state: RootState) => state.areas[id];

export const initialState: RootState = {
    rootArea: "/",
    areas: {
        "/": {
            id: "/",
            control: {
                componentTypeName: "LayoutStackControl",
                props: {
                    skin: "MainWindow",
                    areaIds: [
                        "/Main",
                        "/Toolbar"
                    ]
                }
            }
        },
        "/Main": {
            id: "/Main",
            control: {
                componentTypeName: "MenuItemControl",
                props: {
                    title: "Hello world",
                    icon: "systemorph-fill"
                }
            }
        },
        "/Toolbar": {
            id: "",
            control: {
                componentTypeName: "TextBoxControl",
                props: {
                    data: "Hello world"
                }
            }
        }
    }
}