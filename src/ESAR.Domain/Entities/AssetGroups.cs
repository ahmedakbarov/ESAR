using Esar.Domain.Common;

namespace Esar.Domain.Entities;

/// <summary>
/// A named, manually-curated collection of assets. A compliance policy can target a group directly
/// (via its scope), so operators can apply a security baseline to an explicit, hand-picked set of
/// assets rather than only rule-based scopes (asset type, environment, tags, …).
/// </summary>
public class AssetGroup : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ICollection<AssetGroupMember> Members { get; set; } = new List<AssetGroupMember>();
}

/// <summary>Membership join between an asset and a group (many-to-many).</summary>
public class AssetGroupMember
{
    public Guid AssetGroupId { get; set; }
    public AssetGroup? Group { get; set; }
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public string AddedBy { get; set; } = "system";
}
