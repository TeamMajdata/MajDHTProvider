#nullable enable

namespace MajDHTProvider.Publishing
{
    /// <summary>The latest record the publisher signed for one key.</summary>
    /// <param name="Revision">The signed revision.</param>
    /// <param name="FileHash">The published complete-file hash, or null for a withdrawal.</param>
    /// <param name="Withdrawn">Whether the latest record is a withdrawal.</param>
    internal sealed record PublicationLedgerEntry(long Revision, string? FileHash, bool Withdrawn);
}
