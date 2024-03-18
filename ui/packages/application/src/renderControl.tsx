import { camelCase, isString, memoize, upperFirst } from 'lodash';
import React, { ComponentType } from "react";
import { ControlContext } from "./ControlContext";
import { bindIteratee } from "./dataBinding/bind";
import { ControlDef } from "./ControlDef";
import { lazy, Suspense, createElement } from "react";
import { useAppDispatch } from "./app/hooks";

export function renderControl(control: ControlDef) {
    return (
        <Suspense fallback={<div>Loading...</div>}>
            <RenderControl control={control} />
        </Suspense>
    );
}

interface RenderControlProps {
    control: ControlDef;
}

function RenderControl({control}: RenderControlProps) {
    const dispatch = useAppDispatch();
    const {$type} = control;
    const view: any = control;

    const name = getComponentName($type);
    const componentType = getControlComponent(name);

    function onChange(path: string, value: unknown) {
        dispatch(updateView(path, value));
    }

    return (
            <ControlContext
                onChange={onChange}
                boundView={view}
                rawView={view}
                controlName={name}
            >
                {createElement(componentType, view)}
            </ControlContext>
    );
}

export const getControlComponent = memoize((name: string) => {
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