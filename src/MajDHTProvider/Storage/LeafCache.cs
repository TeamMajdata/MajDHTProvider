using System;
using System.IO;
using MajDHTNetwork;
using MajDHTProvider.Charts;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProvider.Storage
{
    /// <summary>
    /// Persists, per chart resource, the Merkle leaves, signed record and file stamp of content the provider serves, so a
    /// restart registers unchanged files with the node from these few kilobytes instead of reading every file again. An
    /// entry is trusted only while its file still has the stamp it was saved with and passes validation. Losing an entry
    /// costs no more than one read of its file, so entries that cannot be read or written are logged and never stop the
    /// provider.
    /// </summary>
    internal sealed class LeafCache
    {
        /// <summary>The name of the cache directory inside the data directory.</summary>
        public const string DirectoryName = "leaves";

        /// <summary>The extension of an entry file.</summary>
        private const string FileExtension = ".leaves";
        /// <summary>The bytes that start every entry file and name its format version.</summary>
        private static readonly byte[] s_magic = "MDL1"u8.ToArray();

        /// <summary>The cache directory.</summary>
        private readonly string _directory;
        /// <summary>Receives cache diagnostics.</summary>
        private readonly ILogger _logger;

        /// <summary>Creates a cache in a directory that is created on the first save.</summary>
        /// <param name="directory">The cache directory.</param>
        /// <param name="logger">Receives cache diagnostics.</param>
        public LeafCache(string directory, ILogger logger)
        {
            _directory = Path.GetFullPath(directory);
            _logger = logger;
        }

        /// <summary>Reads the entry of a resource.</summary>
        /// <param name="resource">The resource.</param>
        /// <returns>The entry, or null when there is none or it cannot be read or is invalid.</returns>
        public CachedContent? TryLoad(ChartResource resource)
        {
            var path = GetPath(resource);
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);
                var entry = Read(reader, resource.Kind);
                if (entry == null)
                {
                    _logger.LogWarning("The cached leaves of {Key} at {Path} are invalid and are ignored.", resource.KeyName, path);
                }
                return entry;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not read the cached leaves of {Key} at {Path}; the file is read instead.", resource.KeyName, path);
                return null;
            }
        }

        /// <summary>
        /// Writes the entry of a resource, replacing any earlier entry. The file is written in place rather than through a
        /// temporary file, because creating two files per resource dominates the cost of publishing many small files and an
        /// entry cut short by a crash fails validation and only costs one read of its file.
        /// </summary>
        /// <param name="resource">The resource.</param>
        /// <param name="content">The entry.</param>
        public void Save(ChartResource resource, CachedContent content)
        {
            var path = GetPath(resource);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using var writer = new BinaryWriter(stream);
                Write(writer, content);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not save the cached leaves of {Key} to {Path}; the file is read again after a restart.", resource.KeyName, path);
            }
        }

        /// <summary>Replaces the record of an entry after the content was published or renewed under a new record.</summary>
        /// <param name="resource">The resource.</param>
        /// <param name="record">The new record of the same content.</param>
        /// <remarks>Nothing happens when the resource has no entry or its entry describes other content.</remarks>
        public void UpdateRecord(ChartResource resource, DHTRecord record)
        {
            var entry = TryLoad(resource);
            if (entry != null && entry.FileHash == record.FileHash && entry.Length == record.Length)
            {
                Save(resource, entry with { Record = record.ToBytes() });
            }
        }

        /// <summary>Deletes the entry of a resource and the chart's cache directory once it is empty.</summary>
        /// <param name="resource">The resource.</param>
        public void Remove(ChartResource resource)
        {
            var path = GetPath(resource);
            try
            {
                File.Delete(path);
                var directory = Path.GetDirectoryName(path)!;
                if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not delete the cached leaves of {Key} at {Path}.", resource.KeyName, path);
            }
        }

        /// <summary>Gets the entry file of a resource: <c>&lt;directory&gt;/&lt;GUID&gt;/&lt;key suffix&gt;.leaves</c>.</summary>
        /// <param name="resource">The resource.</param>
        /// <returns>The full file path.</returns>
        private string GetPath(ChartResource resource)
        {
            return Path.Combine(_directory, ChartResource.FormatChartId(resource.ChartId), ChartResource.GetKeySuffix(resource.Kind) + FileExtension);
        }

        /// <summary>Writes an entry.</summary>
        /// <param name="writer">The entry file.</param>
        /// <param name="content">The entry.</param>
        private static void Write(BinaryWriter writer, CachedContent content)
        {
            writer.Write(s_magic);
            writer.Write((byte)content.Kind);
            writer.Write(content.Stamp.Length);
            writer.Write(content.Stamp.LastWriteTimeUtc.Ticks);
            writer.Write(content.BlockSize);
            writer.Write(content.Length);
            writer.Write(content.FileHash);
            writer.Write(content.Record.Length);
            writer.Write(content.Record);
            writer.Write(content.Leaves.Length);
            writer.Write(content.Leaves);
        }

        /// <summary>Reads and validates an entry.</summary>
        /// <param name="reader">The entry file.</param>
        /// <param name="kind">The resource kind the entry must describe.</param>
        /// <returns>The entry, or null when the file is not a valid entry of the kind.</returns>
        /// <exception cref="IOException">The file cannot be read or ends early.</exception>
        private static CachedContent? Read(BinaryReader reader, ChartResourceKind kind)
        {
            if (!reader.ReadBytes(s_magic.Length).AsSpan().SequenceEqual(s_magic) || reader.ReadByte() != (byte)kind)
            {
                return null;
            }
            var stamp = new FileStamp(reader.ReadInt64(), new DateTime(reader.ReadInt64(), DateTimeKind.Utc));
            var blockSize = reader.ReadInt32();
            var length = reader.ReadInt64();
            var fileHash = reader.ReadString();
            var recordLength = reader.ReadInt32();
            if (blockSize <= 0 || length < 0 || fileHash.Length != LeafId.Length * 2 || recordLength <= 0 || recordLength > DHTRecord.MaxEncodedLength)
            {
                return null;
            }
            var record = reader.ReadBytes(recordLength);
            var leavesLength = reader.ReadInt32();
            var blockCount = Math.Max(1L, (length + blockSize - 1) / blockSize);
            if (record.Length != recordLength || leavesLength != blockCount * LeafId.Length)
            {
                return null;
            }
            var leaves = reader.ReadBytes(leavesLength);
            if (leaves.Length != leavesLength || reader.BaseStream.Position != reader.BaseStream.Length)
            {
                return null;
            }
            return new CachedContent(kind, stamp, blockSize, length, fileHash, record, leaves);
        }
    }
}
