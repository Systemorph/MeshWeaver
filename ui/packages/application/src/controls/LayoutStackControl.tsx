import React, { useEffect, useState } from 'react';
import { Area } from "../Area";
import { AreaChangedEvent } from "../application.contract";
import { MainWindow } from "./MainWindow";
import classNames from "classnames";
import styles from "./layoutStackControl.module.scss";
import { ModalWindow } from "./ModalWindow";
import { ControlView } from "../ControlDef";
import { useSubscribeToAreaChanged } from "../useSubscribeToAreaChanged";
import { insertAfter } from "@open-smc/utils/src/insertAfter";

export type StackSkin = "VerticalPanel" | "HorizontalPanel" | "HorizontalPanelEqualCols" | "Toolbar" | "SideMenu" | "ContextMenu" | "MainWindow" |
    "Action" | "Modal" | "GridLayout";

export interface StackView extends ControlView {
    areas: AreaChangedEvent[];
    skin?: StackSkin;
    highlightNewAreas?: boolean;
    columnCount?: number;
}

export type StackOptions = {
    insertAfter?: string;
}

export default function LayoutStackControl(props: StackView) {
    const {skin} = props;

    if (skin === "MainWindow") {
        return (
            <MainWindow {...props}/>
        );
    }

    if (skin === "Modal") {
        return (
            <ModalWindow {...props}/>
        );
    }

    return <LayoutStack {...props}/>;
}

function LayoutStack({id, skin, areas: areasInit, style, highlightNewAreas, columnCount}: StackView) {
    const [areas, setAreas] = useState(areasInit);
    const [addedAreas, setAddedAreas] = useState([]);

    useEffect(() => {
        setAreas(areasInit);
        setAddedAreas([]);
    }, [areasInit]);

    useSubscribeToAreaChanged<StackOptions>(event => {
        const {area, options} = event;

        if (!areas?.find(a => a.area === area)) {
            const insertAfterArea = options?.insertAfter;
            const insertAfterEvent = insertAfterArea && areas?.find(a => a.area === insertAfterArea);
            setAreas(areas ? insertAfter(areas, event, insertAfterEvent) : [event]);
            setAddedAreas([...addedAreas, area]);
        }
    });

    const elements = areas?.map(event => {
        const {area, view} = event;

        const className = classNames(styles.stackItem, {
            isAdded: addedAreas.includes(area)
        });

        if (!view) {
            return null;
        }

        return (
            <div style={event.style} className={className} key={area}>
                <Area event={event}/>
            </div>
        );
    });

    const containerClassName = getStackClassNames(skin, highlightNewAreas);

    const cssVars = {
        ["--columnNumber"]: `${areas.length}`,
        ["--columnCount"]: columnCount,
    };

    return (
        <div id={id} className={containerClassName} style={{...style, ...cssVars}}>
            {elements}
        </div>
    );
}

export function getStackClassNames(skin: StackSkin, highlightNewAreas: boolean) {
    return classNames(styles.container, {
        [`skin-${skin}`]: skin,
        highlightNewAreas
    });
}
