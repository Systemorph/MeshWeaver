import { useSelector, useStore } from "../SideMenuStore";
import { SideMenuState } from "../SideMenuState";

const keepSideMenuOpenSelector = (state: SideMenuState) => state.keepOpen;

export function useKeepSideMenuOpen() {
    const {setState, notify} = useStore();
    const keepSideMenuOpen = useSelector(keepSideMenuOpenSelector);

    const setKeepSideMenuOpen = (keepOpen: boolean) => {
        setState({keepOpen});
        notify(keepSideMenuOpenSelector);
    }

    return {keepSideMenuOpen, setKeepSideMenuOpen};
}