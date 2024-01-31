import { useStore } from "./envSettingsStore";

export function useEnvSettingsState() {
    const {getState} = useStore();
    return getState();
}