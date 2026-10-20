using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;

#nullable enable

namespace MajDHTProvider.Storage
{
    /// <summary>
    /// Serves DHT blocks directly from chart files on disk instead of copying them into memory. Each indexed file is split
    /// into blocks whose leaf identifiers follow the MajDHT v1 rule <c>SHA256(0x00 || block)</c>; a read rehashes the block, so
    /// a file changed after indexing is never served as stale content. Blocks that are not backed by an indexed file, such
    /// as blocks of a download that has not been moved into place yet, are kept in a bounded memory cache. The index holds
    /// identifiers as 32-byte values and block positions as small structures, so a library of millions of blocks fits in a
    /// few hundred megabytes.
    /// </summary>
    internal sealed class FileBlockStore : IDHTContentStore
    {
        /// <summary>Compares file paths like the host file system does.</summary>
        private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>Protects the block and file indexes.</summary>
        private readonly Lock _gate = new();
        /// <summary>Every indexed block, keyed by leaf identifier.</summary>
        private readonly Dictionary<LeafId, BlockEntry> _blocks = new();
        /// <summary>The indexed files, keyed by full path.</summary>
        private readonly Dictionary<string, IndexedFile> _files = new(s_pathComparer);
        /// <summary>Holds blocks that are not backed by an indexed file.</summary>
        private readonly MemoryDHTContentStore _cache;

        /// <summary>Creates an empty store.</summary>
        /// <param name="cacheCapacity">The byte capacity of the memory cache for blocks without a backing file.</param>
        /// <exception cref="ArgumentOutOfRangeException">The capacity is not positive.</exception>
        public FileBlockStore(long cacheCapacity)
        {
            _cache = new MemoryDHTContentStore(cacheCapacity);
        }

        /// <summary>Gets the number of files whose blocks are indexed, including files still being indexed.</summary>
        public int IndexedFileCount
        {
            get
            {
                lock (_gate)
                {
                    return _files.Count;
                }
            }
        }

        /// <summary>Computes the MajDHT v1 leaf identifier of a raw block.</summary>
        /// <param name="block">The raw block.</param>
        /// <returns>The lowercase hexadecimal <c>SHA256(0x00 || block)</c>.</returns>
        public static string ComputeLeafId(ReadOnlySpan<byte> block)
        {
            return ComputeLeaf(block).ToString();
        }

        /// <summary>
        /// Opens a file for one sequential pass that indexes its blocks while they are read, replacing any earlier index of
        /// the path. Hand the stream to the node and finish with <see cref="IndexingFileStream.CompleteAsync"/>, so the file
        /// is read once for both the node and the index.
        /// </summary>
        /// <param name="path">The file path.</param>
        /// <param name="blockSize">The block size of the content geometry.</param>
        /// <returns>The stream, which the caller disposes.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The block size is not positive.</exception>
        /// <exception cref="IOException">The file cannot be opened.</exception>
        /// <exception cref="UnauthorizedAccessException">The file is not readable.</exception>
        public IndexingFileStream OpenIndexing(string path, int blockSize)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
            var fullPath = Path.GetFullPath(path);
            var inner = OpenShared(fullPath);
            var file = new IndexedFile(fullPath);
            lock (_gate)
            {
                RemoveLocked(fullPath);
                _files[fullPath] = file;
            }
            return new IndexingFileStream(this, file, inner, blockSize);
        }

        /// <summary>
        /// Hashes a file and indexes its blocks so they are served from the file, replacing any earlier index of the path.
        /// </summary>
        /// <param name="path">The file path.</param>
        /// <param name="blockSize">The block size of the content geometry.</param>
        /// <param name="cancellationToken">Cancels reading.</param>
        /// <returns>The complete-file hash and length.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The block size is not positive.</exception>
        /// <exception cref="IOException">The file cannot be read.</exception>
        /// <exception cref="UnauthorizedAccessException">The file is not readable.</exception>
        /// <exception cref="OperationCanceledException">Indexing was cancelled.</exception>
        public async Task<FileDigest> IndexFileAsync(string path, int blockSize, CancellationToken cancellationToken)
        {
            await using var stream = OpenIndexing(path, blockSize);
            return await stream.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Stops serving blocks from a file.</summary>
        /// <param name="path">The file path.</param>
        /// <returns>Whether the file was indexed.</returns>
        public bool Remove(string path)
        {
            lock (_gate)
            {
                return RemoveLocked(Path.GetFullPath(path));
            }
        }

        /// <summary>Caches a block unless an indexed file already provides it.</summary>
        /// <param name="blockHash">The leaf identifier.</param>
        /// <param name="content">The raw block, copied when cached.</param>
        /// <param name="cancellationToken">Cancels storage work.</param>
        /// <returns>Completion of the write.</returns>
        /// <exception cref="ArgumentException">The identifier or content is invalid.</exception>
        /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
        public ValueTask PutAsync(string blockHash, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            if (LeafId.TryParse(blockHash, out var leafId))
            {
                lock (_gate)
                {
                    if (_blocks.ContainsKey(leafId))
                    {
                        return ValueTask.CompletedTask;
                    }
                }
            }
            return _cache.PutAsync(blockHash, content, cancellationToken);
        }

        /// <summary>Reads a block from the first indexed file that still contains it, or from the memory cache.</summary>
        /// <param name="blockHash">The leaf identifier.</param>
        /// <param name="cancellationToken">Cancels storage work.</param>
        /// <returns>A verified copy of the block, or null when no source holds it.</returns>
        /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
        public async ValueTask<ReadOnlyMemory<byte>?> GetAsync(string blockHash, CancellationToken cancellationToken)
        {
            if (LeafId.TryParse(blockHash, out var leafId))
            {
                foreach (var candidate in GetLocations(leafId))
                {
                    var block = await TryReadAsync(candidate, leafId, cancellationToken).ConfigureAwait(false);
                    if (block != null)
                    {
                        return block;
                    }
                }
            }
            return await _cache.GetAsync(blockHash, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Indexes one block of a file that is being read through an <see cref="IndexingFileStream"/>.</summary>
        /// <param name="file">The index entry of the file.</param>
        /// <param name="leafId">The leaf identifier of the block.</param>
        /// <param name="offset">The byte offset of the block.</param>
        /// <param name="length">The exact block length.</param>
        /// <remarks>A block of a file whose index was removed or replaced since the stream was opened is ignored.</remarks>
        internal void AddBlock(IndexedFile file, LeafId leafId, long offset, int length)
        {
            lock (_gate)
            {
                if (!_files.TryGetValue(file.Path, out var current) || current != file)
                {
                    return;
                }
                file.Leaves.Add(leafId);
                var location = new BlockLocation(file, offset, length);
                if (!_blocks.TryGetValue(leafId, out var entry))
                {
                    _blocks[leafId] = new BlockEntry(location, null);
                    return;
                }
                var others = entry.Others ?? [];
                others.Add(location);
                _blocks[leafId] = entry with { Others = others };
            }
        }

        /// <summary>Computes the MajDHT v1 leaf identifier of a raw block.</summary>
        /// <param name="block">The raw block.</param>
        /// <returns>The identifier.</returns>
        private static LeafId ComputeLeaf(ReadOnlySpan<byte> block)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData([0]);
            hash.AppendData(block);
            Span<byte> digest = stackalloc byte[LeafId.Length];
            hash.GetHashAndReset(digest);
            return LeafId.FromDigest(digest);
        }

        /// <summary>Opens a file for sequential reading while other processes may still write, rename or delete it.</summary>
        /// <param name="path">The full file path.</param>
        /// <returns>The open stream.</returns>
        /// <exception cref="IOException">The file cannot be opened.</exception>
        /// <exception cref="UnauthorizedAccessException">The file is not readable.</exception>
        private static FileStream OpenShared(string path)
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous
            });
        }

        /// <summary>Reads one block from a file and checks that it still has the expected leaf identifier.</summary>
        /// <param name="location">The indexed location.</param>
        /// <param name="leafId">The expected leaf identifier.</param>
        /// <param name="cancellationToken">Cancels reading.</param>
        /// <returns>The block, or null when the file is missing, shorter or changed.</returns>
        /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
        private static async Task<byte[]?> TryReadAsync(BlockLocation location, LeafId leafId, CancellationToken cancellationToken)
        {
            try
            {
                await using var stream = OpenShared(location.File.Path);
                stream.Position = location.Offset;
                var block = new byte[location.Length];
                var count = await stream.ReadAtLeastAsync(block, block.Length, false, cancellationToken).ConfigureAwait(false);
                return count == block.Length && ComputeLeaf(block) == leafId ? block : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Copies the indexed locations of a block, the first indexed file first.</summary>
        /// <param name="leafId">The leaf identifier.</param>
        /// <returns>The locations; empty when no indexed file contains the block.</returns>
        private List<BlockLocation> GetLocations(LeafId leafId)
        {
            lock (_gate)
            {
                if (!_blocks.TryGetValue(leafId, out var entry))
                {
                    return [];
                }
                var locations = new List<BlockLocation> { entry.First };
                if (entry.Others != null)
                {
                    locations.AddRange(entry.Others);
                }
                return locations;
            }
        }

        /// <summary>Removes every block location of a file.</summary>
        /// <param name="fullPath">The full file path.</param>
        /// <returns>Whether the file was indexed.</returns>
        /// <remarks>The caller must hold <see cref="_gate"/>.</remarks>
        private bool RemoveLocked(string fullPath)
        {
            if (!_files.Remove(fullPath, out var file))
            {
                return false;
            }
            foreach (var leafId in file.Leaves)
            {
                RemoveLocationLocked(leafId, file);
            }
            return true;
        }

        /// <summary>Removes the locations of one block that point into a file, keeping the locations in other files.</summary>
        /// <param name="leafId">The leaf identifier.</param>
        /// <param name="file">The file whose locations are removed.</param>
        /// <remarks>The caller must hold <see cref="_gate"/>.</remarks>
        private void RemoveLocationLocked(LeafId leafId, IndexedFile file)
        {
            if (!_blocks.TryGetValue(leafId, out var entry))
            {
                return;
            }
            var others = entry.Others;
            others?.RemoveAll(location => location.File == file);
            if (others != null && others.Count == 0)
            {
                others = null;
            }
            if (entry.First.File != file)
            {
                _blocks[leafId] = entry with { Others = others };
            }
            else if (others == null)
            {
                _blocks.Remove(leafId);
            }
            else
            {
                var first = others[0];
                others.RemoveAt(0);
                _blocks[leafId] = new BlockEntry(first, others.Count == 0 ? null : others);
            }
        }

        /// <summary>An indexed file and the leaf identifiers of its blocks, in file order.</summary>
        internal sealed class IndexedFile
        {
            /// <summary>Creates an entry without blocks.</summary>
            /// <param name="path">The full file path.</param>
            internal IndexedFile(string path)
            {
                Path = path;
            }

            /// <summary>Gets the full file path.</summary>
            internal string Path { get; }

            /// <summary>Gets the leaf identifiers of the indexed blocks, in file order; the store's gate protects the list.</summary>
            internal List<LeafId> Leaves { get; } = [];
        }

        /// <summary>The position of one block inside an indexed file.</summary>
        /// <param name="File">The indexed file.</param>
        /// <param name="Offset">The byte offset of the block.</param>
        /// <param name="Length">The exact block length.</param>
        private readonly record struct BlockLocation(IndexedFile File, long Offset, int Length);

        /// <summary>The locations of one block: the first indexed file that contains it and, rarely, further files with the same block.</summary>
        /// <param name="First">The location in the first indexed file.</param>
        /// <param name="Others">The locations in further files, or null when the block is in one file only.</param>
        private readonly record struct BlockEntry(BlockLocation First, List<BlockLocation>? Others);
    }
}
