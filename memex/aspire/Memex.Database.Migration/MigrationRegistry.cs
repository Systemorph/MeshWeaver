using System;
using System.Linq;
using Memex.Database.Migration.Migrations;

// The single source of truth for versioned migrations. Program runs exactly this list, and
// RegisteredMigrationsTest pins that EVERY V##_* IMigration in this assembly appears here —
// the V42 incident (class present, never registered, silently skipped) cannot recur.
public static class MigrationRegistry
{
    public static readonly IMigration[] All =
    [
        new V01_MoveAccessAssignments(),
        new V02_RebuildTriggerFunctions(),
        new V03_DropRogueSchemas(),
        new V04_UpgradeViewerToAdmin(),
        new V05_EnsureUserSelfAssignments(),
        new V06_FixSearchAcrossSchemas(),
        new V07_PerUserPermissionRebuildTrigger(),
        new V08_FixThreadMessageMainNode(),
        new V09_RenameSourceTestSegments(),
        new V10_PerUserPartitions(),
        new V11_RewriteApiTokenPaths(),
    // v12 was retired — see V13_RebuildPermissionsForApiBitmask for context.
        new V13_RebuildPermissionsForApiBitmask(),
        new V14_AddPartitionPrefixToNamespaces(),
        new V15_FinalUserSchemaCleanup(),
        new V16_NormalizeAccessAssignmentShape(),
        new V17_EnsurePerUserSelfAssignments(),
        new V18_BackfillUserPartitionRegistry(),
        new V19_DeleteLegacyReleaseNodes(),
        new V20_RemoveStrayLegacyUserRows(),
    // v21 retired -- gap preserved so existing prod db_version counters stay monotonic.
        new V22_ConsolidateGlobalCatalogsInAdmin(),
        new V23_PartitionChangesNotify(),
        new V24_DedupMeshNodeNotifyTrigger(),
        new V25_MirrorAccessObjectsToUserSchema(),
        new V26_AddNotificationsSatelliteTable(),
        new V27_RenameUserSchemaToAuthAndMirrorApiTokens(),
        new V28_RenameOrganizationToSpace(),
        new V29_PinDocsForExistingUsers(),
        new V30_EnsurePartitionSchemaStoredProc(),
        new V31_UnifyUserMirrorIntoAuthAndRelocateContent(),
        new V32_RepairAuthMirrorTriggerAndBackfill(),
        new V33_SeedThreadComposerForExistingUsers(),
        new V34_TypeOrphanPartitionRootsAsSpace(),
        new V35_ReconcilePartitionAccessIndex(),
        new V36_MoveAgentsToPerPartitionAgentNamespace(),
        new V37_MoveAgentsToAgentNamespaceBySchema(),
        new V38_DropLegacyProviderSchema(),
        new V39_AddSyncBehaviorColumn(),
        new V40_CreateEventLogSchema(),
        new V41_RetrofitModelCatalogIcons(),
        new V42_ReapplySpaceAuthMirrorAndBackfill(),
        new V43_FixAccessTriggerSchemaResolutionAndBackfill(),
    ];

    /// <summary>
    /// Fails the migration run LOUDLY when a V##_* migration class exists in this assembly but is
    /// not registered above — the V42 incident (class shipped, never registered, silently skipped
    /// for days) must crash the pod instead of passing unnoticed.
    /// </summary>
    public static void VerifyComplete()
    {
        var registered = All.Select(m => m.GetType()).ToHashSet();
        var missing = typeof(MigrationRegistry).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t)
                        && System.Text.RegularExpressions.Regex.IsMatch(t.Name, @"^V\d+_"))
            .Where(t => !registered.Contains(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "Migration classes exist but are NOT registered in MigrationRegistry.All "
                + "(they would be silently skipped): " + string.Join(", ", missing.Select(t => t.Name)));
    }
}
