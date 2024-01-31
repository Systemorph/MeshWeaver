import React from 'react'
import { Html } from "@open-smc/ui-kit/components/Html";
import { ControlView } from "../ControlDef";
import "./HtmlControl.module.scss";

export interface HtmlView extends ControlView {
    data?: string;
}

export default function HtmlControl({id, data, style}: HtmlView) {
    return <Html id={id} html={data} style={style}/>
}
