using Thor.DataLayer.Models.Tenants;
using Thor.Graph;

namespace Thor.Workflows.GraphLoad.Persistence;

/// <summary>What of an account, group or asset row becomes a vertex's properties.</summary>
internal static class GraphVertexProjection
{
    public static GraphVertexUpsert ToUpsert(Account account) => new(GraphLoadKinds.Account, account.Id, new Dictionary<string, object?>
    {
        ["nativeId"] = account.NativeId,
        ["connectorType"] = account.ConnectorType,
        ["accountKind"] = account.AccountKind,
        ["isHuman"] = account.IsHuman,
        ["displayName"] = account.DisplayName,
        ["samAccountName"] = account.SamAccountName,
        ["upn"] = account.Upn,
        ["email"] = account.Email,
        ["domainName"] = account.DomainName,
        ["isDeleted"] = account.IsDeleted,
        ["isDisabled"] = account.IsDisabled,
    });

    public static GraphVertexUpsert ToUpsert(Grp grp) => new(GraphLoadKinds.Group, grp.Id, new Dictionary<string, object?>
    {
        ["nativeId"] = grp.NativeId,
        ["connectorType"] = grp.ConnectorType,
        ["groupClass"] = grp.GroupClass,
        ["displayName"] = grp.DisplayName,
        ["email"] = grp.Email,
        ["domainName"] = grp.DomainName,
        ["isLargeGroup"] = grp.IsLargeGroup,
        ["isDeleted"] = grp.IsDeleted,
    });

    public static GraphVertexUpsert ToUpsert(Asset asset) => new(GraphLoadKinds.Asset, asset.Id, new Dictionary<string, object?>
    {
        ["nativeId"] = asset.NativeId,
        ["connectorType"] = asset.ConnectorType,
        ["assetType"] = asset.AssetType,
        ["displayName"] = asset.DisplayName,
        ["fullPath"] = asset.FullPath,
        ["filerName"] = asset.FilerName,
        ["fileSize"] = asset.FileSize,
        ["fileCount"] = asset.FileCount,
        ["brokenAcl"] = asset.BrokenAcl,
        ["isProtected"] = asset.IsProtected,
    });
}
