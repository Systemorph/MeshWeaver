import { useKeepSideMenuOpen } from "../../../../components/sideMenu/hooks/useKeepSideMenuOpen";
import { useFileExplorerSelector, useFileExplorerStore } from "../../ProjectExplorerContextProvider";

export function useUploadDialog() {
    const fileExplorerStore = useFileExplorerStore();
    const {setKeepSideMenuOpen} = useKeepSideMenuOpen()

    const uploadFormVisible = useFileExplorerSelector("uploadFormVisible");

    const openUploadForm = () => {
        setKeepSideMenuOpen(true);

        fileExplorerStore.setState(state => {
            state.uploadFormVisible = true;
        });
    }

    const closeUploadForm = () => {
        setKeepSideMenuOpen(false);
        fileExplorerStore.setState(state => {
            state.uploadFormVisible = true;
        });
    }

    return {uploadFormVisible, openUploadForm, closeUploadForm}
}
