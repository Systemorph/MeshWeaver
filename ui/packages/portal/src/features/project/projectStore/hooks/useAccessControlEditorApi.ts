import { getAccessControlEditorApi } from "../../../accessControl/accessControlEditorApi";
import { useViewModelId } from "../../projectStore/hooks/useViewModelId";

export function useAccessControlEditorApi() {
    const viewModelId = useViewModelId();
    return getAccessControlEditorApi(viewModelId);
}