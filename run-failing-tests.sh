#!/bin/bash
# Runs only the tests in failing-tests.txt, project by project.
# Output goes to test-run-results.txt (overwritten each run).

OUT=test-run-results.txt
echo "=== Targeted re-run started: $(date) ===" > $OUT

# Build each project's filter from failing-tests.txt (skip comments + empty lines)
run_proj() {
    local proj=$1
    local proj_path=$2
    shift 2
    local filters=("$@")
    local filter_str=""
    for t in "${filters[@]}"; do
        if [ -z "$filter_str" ]; then
            filter_str="FullyQualifiedName~$t"
        else
            filter_str="$filter_str|FullyQualifiedName~$t"
        fi
    done
    echo "" >> $OUT
    echo "=== $proj ($(echo "${filters[@]}" | wc -w) tests) ===" >> $OUT
    dotnet test "$proj_path" --no-restore --filter "$filter_str" 2>&1 \
        | grep -E "^(Failed|Passed)!|\[FAIL\]|Error Message:" >> $OUT
    echo "(returned $?)" >> $OUT
}

# ===== Run each project ===== (FutuRe excluded — owned by another agent)

run_proj "Markdown.Test" "test/MeshWeaver.Markdown.Test" \
  "MultipleBlocks_ShareKernelState_ViaSharedAddress"

run_proj "AccessControl.Test" "test/MeshWeaver.AccessControl.Test" \
  "Overview_RendersChangeSubjectButton" \
  "Thumbnail_ClickRemoveRole_RemovesChip" \
  "UpdateAccessObject_ChangesSubject_ViaDataChange"

run_proj "Insurance.Test" "samples/Insurance/MeshWeaver.Insurance.Test" \
  "GetPricingCatalog_UsingLayoutAreaReference_ShouldReturnPricingsControl" \
  "GetPricingCatalog_ShouldReturnPricings"

run_proj "Todo.Test" "test/MeshWeaver.Todo.Test" \
  "Step1_SetupDataContext_WithTodoItems"

run_proj "Threading.Test" "test/MeshWeaver.Threading.Test" \
  "UpdateMeshNode_MultipleUpdates_AccumulateMessages" \
  "SubmitMessage_WithToolCalling_ExecutesSearchAndReturnsResult"

run_proj "Content.Test" "test/MeshWeaver.Content.Test" \
  "VersionsArea_SingleVersion_RendersWithoutError" \
  "VersionsMenu_AppearsInNodeMenu" \
  "VersionsArea_RendersVersionList"

run_proj "Persistence.Test" "test/MeshWeaver.Persistence.Test" \
  "ResolvePathAsync_UnknownPath_ShouldReturnNull" \
  "ResolvePathAsync_FutuReSubPaths_ShouldResolve" \
  "MarkdownNode_LoadsWithoutHanging" \
  "InteractiveShowcaseMd_FullPipeline_AllBlocksExecute" \
  "MultipleSubmissions_ShareKernelState" \
  "Move_LargeSubtree_RunsIOInParallel"

run_proj "Hosting.Blazor.Test" "test/MeshWeaver.Hosting.Blazor.Test" \
  "OnLocationChanged_SatelliteNode_CurrentNamespacePointsAtMainNode" \
  "OnLocationChanged_SatelliteNode_LoadsCreatableTypesForMainNode"

run_proj "Auth.Test" "test/MeshWeaver.Auth.Test" \
  "ValidateToken_ValidToken_ReturnsApiToken" \
  "ValidateToken_RevokedToken_ReturnsNull"

run_proj "Security.Test" "test/MeshWeaver.Security.Test" \
  "SubscribeRequest_WithReadPermission_Succeeds" \
  "SubscribeRequest_WithoutReadPermission_ReturnsDeliveryFailure" \
  "GetDataRequest_WithoutReadPermission_ReturnsDeliveryFailure" \
  "McpSearch_User1SeesOnlyPermittedNodes" \
  "McpUpdate_User1CannotUpdatePrivateOrg_User2Can" \
  "McpGet_User1CanReadPublicNode" \
  "McpSearch_User1CannotSearchPrivateOrg" \
  "McpUpdate_User1CannotUpdate_User2Can" \
  "McpGet_User1CannotReadPrivateOrg_User2Can" \
  "McpGet_User1CannotReadConfidentialNode_User2Can"

run_proj "Autocomplete.Test" "test/MeshWeaver.Autocomplete.Test" \
  "CanCreateTypeAtPath_ReturnsTrueForValidType" \
  "GetCreatableTypes_DifferentNodesDifferentTypes" \
  "GetCreatableTypes_ReturnsTypesForNode" \
  "FilterByCreatableType_ReturnsOnlyMatchingNodes" \
  "Integration_AutocompleteWithTypeFilter_WorksEndToEnd" \
  "CanCreateTypeAtPath_ReturnsFalseForInvalidType" \
  "LocalFirst_ChildrenOfContextScoreHigherThanDistant"

run_proj "NodeOperations.Test" "test/MeshWeaver.NodeOperations.Test" \
  "CreateApiToken_ViaCreateNodeRequest_Succeeds" \
  "CreateApiToken_StoredUnderUserPath" \
  "CreateNodeAsync_ReplyNode_ShouldLinkToParent"

run_proj "Hosting.PostgreSql.Test" "test/MeshWeaver.Hosting.PostgreSql.Test" \
  "CreateOrganization_HasPermission_ReturnsAdmin"

run_proj "Query.Test" "test/MeshWeaver.Query.Test" \
  "ObserveQuery_EmitsRemovedOnDeletedNode" \
  "ObserveQuery_VersionIncrementsWithEachChange" \
  "ContentEmailQuery_NameCanOverrideClaim" \
  "PropertyChange_NoLongerMatchesQuery_RemovesFromCollection" \
  "AtText_ReturnsCurrentNodeAndGlobal" \
  "GetRemoteStream_AfterDispose_ReturnsFreshInstance" \
  "Catalog_NodeTypeFilter_FiltersCorrectly" \
  "Catalog_Pagination_LoadsMoreItems" \
  "Catalog_TextSearch_FiltersResults"

run_proj "Acme.Test" "test/MeshWeaver.Acme.Test" \
  "DescendantsSearch_FindsOrganizationRootNode" \
  "AcmeOrganization_IsAccessibleToAuthenticatedUser" \
  "SubtreeSearch_FindsOrganizationRootNode" \
  "TodoDataChangeWorkflowTest"

run_proj "Hosting.Orleans.Test" "test/MeshWeaver.Hosting.Orleans.Test" \
  "SubHub_WithExportTypesRegistered_DeserializesPolymorphicExportDocumentControl" \
  "ExportPdfArea_RendersExportDocumentControl_ClientDeserializes" \
  "ToolCall_DuringStreaming_DoesNotDeadlock"

echo "" >> $OUT
echo "=== Run finished: $(date) ===" >> $OUT
