import { appMessage$ } from "./store/appStore";

export function useClickAction(clickMessage: unknown) {
    if (!clickMessage) {
        return null;
    }

    return () => appMessage$.next(clickMessage);
}