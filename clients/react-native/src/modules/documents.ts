// DOCUMENTS module — the file/document surfaces: FileBrowser, DocumentSource, ExportDocument,
// NodeExport/NodeImport.
import type { DeploymentModule } from "@meshweaver/react/core";
import { rnDocumentControls } from "../rnDocuments";

const documents: DeploymentModule = { name: "documents", pack: { controls: { ...rnDocumentControls } } };
export default documents;
