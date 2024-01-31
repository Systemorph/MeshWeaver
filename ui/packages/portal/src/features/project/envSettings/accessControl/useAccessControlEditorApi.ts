import { getAccessControlEditorApi } from "../../../accessControl/accessControlEditorApi";
import { useEnvSettingsState } from "../useEnvSettingsState";

export function useAccessControlEditorApi() {
    const {viewModelId} = useEnvSettingsState();
    return getAccessControlEditorApi(viewModelId);
}