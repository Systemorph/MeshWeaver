import { ControlView } from "../ControlDef";

export type TitleSize = 1 | 2 | 3 | 4 | 5;

// TODO: to be removed as it replicates the HTML elements (9/12/2023, akravets)
export interface TitleView extends ControlView {
    size?: TitleSize;
    data: string;
}

export default function TitleControl({id, size, data, style}: TitleView) {
    const Heading = `h${size}` as keyof JSX.IntrinsicElements;

    return (
        <Heading id={id} style={style}>{data}</Heading>
    );
}