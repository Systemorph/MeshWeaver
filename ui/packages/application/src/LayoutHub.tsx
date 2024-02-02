import { PropsWithChildren, useMemo } from "react";
import { LayoutAddress } from "./application.contract";
import { AddHub } from "./messageHub/AddHub";
import { useConnectionSelector } from "./SignalrConnectionProvider";

export const layoutHubId = "LayoutHub";

export function LayoutHub({children}: PropsWithChildren) {
    const appId = useConnectionSelector("appId");
    const address = useMemo(() => new LayoutAddress(appId), [appId]);

    return (
        <AddHub address={address} children={children} id={layoutHubId}/>
    );
}