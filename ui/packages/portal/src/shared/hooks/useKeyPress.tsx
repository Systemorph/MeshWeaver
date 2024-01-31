import { RefObject } from "react";
import { useEventListener } from "usehooks-ts";

type Handler = (event: KeyboardEvent) => void

export function useKeyPress<T extends HTMLElement = HTMLElement>(
    ref: RefObject<T>,
    handler: Handler,
    key: string,
    event: 'keydown' | 'keyup' = 'keydown',
): void {
    // TODO: Try to use ref with useEventListener to prevent constantly listening to keydown event (5/27/2022, avinokurov)
    useEventListener(event, event => {
        if ((key && key !== event.key)) {
            return;
        }

        handler(event)
    })
}