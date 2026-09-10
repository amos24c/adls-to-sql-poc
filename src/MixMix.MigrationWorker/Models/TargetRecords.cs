namespace MixMix.MigrationWorker.Models;

/// <summary>
/// Target dbo.ItemCategory, sourced from dbo.Category. The target calls the label column Descr, where the
/// source calls it Name.
/// </summary>
public sealed record ItemCategoryRecord(int Id, string? Descr);

/// <summary>Target dbo.Blob, a like-for-like copy of the source dbo.Blob.</summary>
public sealed record BlobRecord(int BlobId, string Name);

/// <summary>Target dbo.Item, a narrowed projection of the source dbo.ItemNew.</summary>
public sealed record ItemRecord(int Id, int CategoryId, string? Name);

/// <summary>Target dbo.ItemBlob, a like-for-like copy of the source dbo.ItemBlob.</summary>
public sealed record ItemBlobRecord(int ItemBlobId, int ItemId, int BlobId);
