import { HTMLAttributes } from "react";
import {Style} from "@open-smc/application/src/Style";

interface Props extends HTMLAttributes<HTMLDivElement>{
    html: string;
    style?: Style;
}

export function Html({html, style, ...props}: Props) {
    return <div dangerouslySetInnerHTML={{__html: html}} style={style} {...props}/>;
}
