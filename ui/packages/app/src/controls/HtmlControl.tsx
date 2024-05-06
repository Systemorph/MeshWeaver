import React from 'react'
import { Html } from "@open-smc/ui-kit/src/components/Html";
import { ControlView } from "../ControlDef";
import "./HtmlControl.module.scss";
import { LayoutAreaModel } from "../store/appStore";

export interface HtmlView extends ControlView {
    data?: string;
}

export default function HtmlControl({props: {id, data, style}}: LayoutAreaModel<HtmlView>) {
    return <Html id={id} html={data} style={style}/>
}
