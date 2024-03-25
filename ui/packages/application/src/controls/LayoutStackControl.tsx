import { MainWindow } from "./MainWindow";
import classNames from "classnames";
import styles from "./layoutStackControl.module.scss";
import { ModalWindow } from "./ModalWindow";
import { ControlView } from "../ControlDef";
import { RenderArea } from "../app/RenderArea";

export type StackSkin =
    "VerticalPanel"
    | "HorizontalPanel"
    | "HorizontalPanelEqualCols"
    | "Toolbar"
    | "SideMenu"
    | "ContextMenu"
    | "MainWindow"
    |
    "Action"
    | "Modal"
    | "GridLayout";

export interface StackView extends ControlView {
    areas: string[];
    skin?: StackSkin;
    highlightNewAreas?: boolean;
    columnCount?: number;
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

function LayoutStack({id, skin, areas, style, highlightNewAreas, columnCount}: StackView) {
    const renderedAreas = areas?.map(id => {
        const className = classNames(
            styles.stackItem, {
                // isAdded: addedAreas.includes(area)
            }
        );

        return (
            <RenderArea id={id} className={className}/>
        );
    });

    const className = getStackClassNames(skin, highlightNewAreas);

    const cssVars = {
        ["--columnNumber"]: `${areas.length}`,
        ["--columnCount"]: columnCount,
    };

    return (
        <div id={id} className={className} style={{...style, ...cssVars}}>
            {renderedAreas}
        </div>
    );
}

export function getStackClassNames(skin: StackSkin, highlightNewAreas: boolean) {
    return classNames(styles.container, {
        [`skin-${skin}`]: skin,
        highlightNewAreas
    });
}