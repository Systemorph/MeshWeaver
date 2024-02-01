import { PropsWithChildren, useMemo } from "react";
import { useConnectionStatus } from "./Connection";
import { LayoutAddress } from "./application.contract";
import { AddHub } from "./messageHub/AddHub";

export const layoutHubId = "LayoutHub";

export function LayoutHub({children}: PropsWithChildren) {
    const {appId} = useConnectionStatus();
    const address = useMemo(() => new LayoutAddress(appId), [appId]);

    return (
        <AddHub address={address} children={children} id={layoutHubId}/>
    );
}