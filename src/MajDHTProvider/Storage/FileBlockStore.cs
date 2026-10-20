using System;
using System.Buffers;
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
    /// as blocks of a download that has not been moved into place yet, are kept in a bounded memory cache.
    /// </summary>
    internal sealed class FileBlockStore : IDHTContentStore
    {
        /// <summary>Compares file paths like the host file system does.</summary>
        private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>Protects the block and file indexes.</summary>
        private readonly Lock _gate = new();
        /// <summary>The file locations of every indexed block, keyed by leaf identifier.</summary>
        private readonly Dictionary<string, List<BlockLocation>> _locations = new(StringComparer.Ordinal);
        /// <summary>The leaf identifiers of every indexed file, keyed by full path.</summary>
        private readonly Dictionary<string, string[]> _files = new(s_pathComparer);
        /// <summary>Holds blocks that are not backed by an indexed file.</summary>
        private readonly MemoryDHTContentStore _cache;

        /// <summary>Creates an empty store.</summary>
        /// <param name="cacheCapacity">The byte capacity of the memory cache for blocks without a backing file.</param>
        /// <exception cref="ArgumentOutOfRangeException">The capacity is not positive.</exception>
        public FileBlockStore(long cacheCapacity)
        {
            _cache = new MemoryDHTContentStore(cacheCapacity);
        }

        /// <summary>Gets the number of files whose blocks are currently indexed.</summary>
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
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData([0]);
            hash.AppendData(block);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
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
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
            var fullPath = Path.GetFullPath(path);
            var blocks = new List<(string LeafId, BlockLocation Location)>();
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(blockSize);
            long length = 0;
            try
            {
                await using var stream = OpenShared(fullPath);
                while (true)
                {
                    var count = await stream.ReadAtLeastAsync(buffer.AsMemory(0, blockSize), blockSize, false, cancellationToken)
                        .ConfigureAwait(false);
                    // An empty file still has one zero-length block; otherwise only the final block may be shorter.
                    if (count == 0 && blocks.Count != 0)
                    {
                        break;
                    }
                    fileHash.AppendData(buffer, 0, count);
                    blocks.Add((ComputeLeafId(buffer.AsSpan(0, count)), new BlockLocation(fullPath, length, count)));
                    length += count;
                    if (count < blockSize)
                    {
                        break;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            lock (_gate)
            {
                RemoveLocked(fullPath);
                var leafIds = new string[blocks.Count];
                for (var index = 0; index < blocks.Count; index++)
                {
                    var (leafId, location) = blocks[index];
                    if (!_locations.TryGetValue(leafId, out var locations))
                    {
                        locations = [];
                        _locations.Add(leafId, locations);
                    }
                    locations.Add(location);
                    leafIds[index] = leafId;
                }
                _files[fullPath] = leafIds;
            }
            return new FileDigest(Convert.ToHexStringLower(fileHash.GetHashAndReset()), length);
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
            lock (_gate)
            {
                if (_locations.ContainsKey(blockHash))
                {
                    return ValueTask.CompletedTask;
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
            BlockLocation[] candidates;
            lock (_gate)
            {
                candidates = _locations.TryGetValue(blockHash, out var locations) ? [.. locations] : [];
            }
            foreach (var candidate in candidates)
            {
                var block = await TryReadAsync(candidate, blockHash, cancellationToken).ConfigureAwait(false);
                if (block != null)
                {
                    return block;
                }
            }
            return await _cache.GetAsync(blockHash, cancellationToken).ConfigureAwait(false);
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
        /// <param name="blockHash">The expected leaf identifier.</param>
        /// <param name="cancellationToken">Cancels reading.</param>
        /// <returns>The block, or null when the file is missing, shorter or changed.</returns>
        /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
        private static async Task<byte[]?> TryReadAsync(BlockLocation location, string blockHash, CancellationToken cancellationToken)
        {
            try
            {
                await using var stream = OpenShared(location.Path);
                stream.Position = location.Offset;
                var block = new byte[location.Length];
                var count = await stream.ReadAtLeastAsync(block, block.Length, false, cancellationToken).ConfigureAwait(false);
                return count == block.Length && ComputeLeafId(block) == blockHash ? block : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Removes every block location of a file.</summary>
        /// <param name="fullPath">The full file path.</param>
        /// <returns>Whether the file was indexed.</returns>
        /// <remarks>The caller must hold <see cref="_gate"/>.</remarks>
        private bool RemoveLocked(string fullPath)
        {
            if (!_files.Remove(fullPath, out var leafIds))
            {
                return false;
            }
            foreach (var leafId in leafIds)
            {
                if (_locations.TryGetValue(leafId, out var locations))
                {
                    locations.RemoveAll(location => s_pathComparer.Equals(location.Path, fullPath));
                    if (locations.Count == 0)
                    {
                        _locations.Remove(leafId);
                    }
                }
            }
            return true;
        }

        /// <summary>The position of one block inside an indexed file.</summary>
        /// <param name="Path">The full file path.</param>
        /// <param name="Offset">The byte offset of the block.</param>
        /// <param name="Length">The exact block length.</param>
        private readonly record struct BlockLocation(string Path, long Offset, int Length);
    }
}
