using System;
using System.IO;
using MajDHTNetwork;
using MajDHTProvider.Charts;

#nullable enable

namespace MajDHTProvider.Storage
{
    /// <summary>
    /// The persisted description of a served resource file: enough to register its content with the node and to serve its
    /// blocks from the file without reading the file again, as long as the file still has the recorded stamp.
    /// </summary>
    /// <param name="Kind">The resource kind the file stores.</param>
    /// <param name="Stamp">The length and last write time the file had when its blocks were hashed.</param>
    /// <param name="BlockSize">The block size the leaves were computed with.</param>
    /// <param name="Length">The file length in bytes.</param>
    /// <param name="FileHash">The lowercase hexadecimal SHA-256 of the complete file.</param>
    /// <param name="Record">The encoded signed record under which the content was last published or served.</param>
    /// <param name="Leaves">The contiguous 32-byte leaf identifiers of every block in file order.</param>
    internal sealed record CachedContent(ChartResourceKind Kind, FileStamp Stamp, int BlockSize, long Length, string FileHash,
        byte[] Record, byte[] Leaves)
    {
        /// <summary>Gets the number of blocks the leaves describe.</summary>
        public int BlockCount => Leaves.Length / LeafId.Length;

        /// <summary>Parses the signed record.</summary>
        /// <returns>The record, or null when it is malformed, forged or expired.</returns>
        public DHTRecord? TryParseRecord()
        {
            try
            {
                return DHTRecord.Parse(Record);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                return null;
            }
        }
    }
}
