import { HTMLAttributes, PropsWithChildren, useRef } from "react";

export type LazyState = 'visible'|'hidden'|'none'

type LazyProps = HTMLAttributes<HTMLDivElement> & {
    state: LazyState;
}

export function Lazy({state, children, ...props}: PropsWithChildren<LazyProps>) {
    const rendered = useRef(state === 'visible');

    if (state === 'visible' && !rendered.current) {
        rendered.current = true;
    }

    if (state === 'none') {
        rendered.current = false;
    }

    if (!rendered.current) {
        return null;
    }

    if (state !== 'visible') {
        props = {style: { display: 'none'}};
    }

    return <div {...props}>{children}</div>;
}