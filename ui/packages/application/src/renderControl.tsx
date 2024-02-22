import { camelCase, isString, memoize, upperFirst } from 'lodash';
import { DataContextProvider, useDataContext } from "./dataBinding/DataContextProvider";
import { AddHub, useMessageHub } from "./AddHub";
import React, { ComponentType, useEffect, useMemo, useState } from "react";
import { ControlContext } from "./ControlContext";
import { isScope } from "./scopes/createScopeMonitor";
import { bind, bindIteratee } from "./dataBinding/bind";
import { ControlDef } from "./ControlDef";
import { useScopeMonitor } from "./scopes/useScopeMonitor";
import { lazy, Suspense, JSX } from "react";
import { DataContext } from "./dataBinding/DataContext";
import { useSubscribeToAreaChanged } from "./useSubscribeToAreaChanged";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";
import { RefreshRequest } from "./contract/application.contract";

export function renderControl(control: ControlDef) {
    const {address} = control;

    return (
        <AddHub address={address}>
            <Suspense fallback={<div>Loading...</div>}>
                <RenderControl
                    control={control}
                    render={control => <RenderControlInner control={control}/>}
                />
            </Suspense>
        </AddHub>
    );
}

interface RootAreaProps {
    control: ControlDef;
    render: (control: ControlDef) => JSX.Element;
}

function RenderControl({control: initialControl, render}: RootAreaProps) {
    const hub = useMessageHub();
    const [control, setControl] = useState(initialControl);

    useSubscribeToAreaChanged(hub, "", ({view}) => setControl(view));

    useEffect(() => {
        sendMessage(hub, new RefreshRequest());
    }, [hub]);

    return render(control);
}

interface RenderControlProps {
    control: ControlDef;
}

function RenderControlInner({control}: RenderControlProps) {
    const {$type, dataContext: dataContextValue} = control;
    const {current, setScopeProperty} = useScopeMonitor(dataContextValue);
    const view = useMemo(() => getView(control), [control]);
    const parentContext = useDataContext();

    const name = getComponentName($type);
    const Component = getControlComponent(name);

    const dataContext = useMemo(
        () => new DataContext(current, parentContext, ({object, key, value}) => {
            if (isScope(object)) {
                setScopeProperty(object.$scopeId, key, value);
            }
        }),
        [current, setScopeProperty, parentContext]
    );

    const boundView = useMemo(() => bind(view, dataContext, controlBinderIteratee),
        [view, dataContext]);

    function onChange(path: string, value: unknown) {
        const viewContext = new DataContext(view, dataContext);
        viewContext.resolveBinding(path)?.set(value);
    }

    return (
        <DataContextProvider dataContext={dataContext}>
            <ControlContext
                onChange={onChange}
                boundView={boundView}
                rawView={view}
                controlName={name}
            >
                <Component {...boundView}/>
            </ControlContext>
        </DataContextProvider>
    );
}

const getControlComponent = memoize((name: string) => {
    for (const resolver of controlResolvers) {
        const controlModule = resolver(name);

        if (controlModule) {
            return lazy(() => controlModule);
        }
    }
})

export function registerControlResolver(resolver: ControlModuleResolver) {
    controlResolvers.push(resolver);
}

export type ControlModuleResolver = (name: string) => Promise<{ default: ComponentType }>;

const controlResolvers: ControlModuleResolver[] = [];

function getComponentName($type: string) {
    const name = $type.split(".").pop();
    return typeToComponentName(name);
}

function typeToComponentName(name: string) {
    if (name === "MultiSelectControl") {
        return "MultiselectControl";
    }
    if (name === "CheckBoxControl") {
        return "CheckboxControl";
    }
    // text/html => TextHtml
    return upperFirst(camelCase(name));
}

function getView(control: ControlDef) {
    const {
        $type,
        moduleName,
        apiVersion,
        address,
        dataContext,
        ...view
    } = control;

    return view;
}

function controlBinderIteratee(value: any) {
    if (isControl(value)) {
        return value;
    }

    return bindIteratee(value);
}

// TODO: find a better way to tell if value is control (9/12/2023, akravets)
function isControl(value: unknown): value is ControlDef {
    const type: string = (value as ControlDef)?.$type;
    return isString(type) && type.endsWith("Control");
}