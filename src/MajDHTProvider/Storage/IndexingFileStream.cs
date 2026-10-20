using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace MajDHTProvider.Storage
{
    /// <summary>
    /// A read-only, forward-only view of a chart file that indexes the file in its <see cref="FileBlockStore"/> while it is
    /// read, so one pass over the file both feeds the DHT node and makes every block servable from disk. A block is indexed
    /// the moment its last byte has been read, before the node asks the store to keep it, so the store recognizes the block
    /// as file-backed instead of copying it into memory. <see cref="CompleteAsync"/> reads whatever the consumer left unread
    /// and returns the complete-file digest; <see cref="ExportLeaves"/> then hands out the leaves for persistence.
    /// </summary>
    internal sealed class IndexingFileStream : Stream
    {
        /// <summary>The domain separation byte that precedes every block in its leaf hash.</summary>
        private static readonly byte[] s_leafPrefix = [0];

        /// <summary>The leaf identifiers of the indexed blocks, in file order.</summary>
        private readonly List<LeafId> _leaves = [];

        /// <summary>The store that receives the indexed blocks.</summary>
        private readonly FileBlockStore _store;
        /// <summary>The index entry of the file being read.</summary>
        private readonly FileBlockStore.IndexedFile _file;
        /// <summary>The file being read.</summary>
        private readonly FileStream _inner;
        /// <summary>The block size of the content geometry.</summary>
        private readonly int _blockSize;
        /// <summary>Hashes the complete file.</summary>
        private readonly IncrementalHash _fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        /// <summary>Hashes the block being read.</summary>
        private readonly IncrementalHash _blockHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        /// <summary>The number of bytes read so far.</summary>
        private long _position;
        /// <summary>The number of bytes of the current block read so far.</summary>
        private int _blockFill;
        /// <summary>Whether the current block has received its domain separation byte.</summary>
        private bool _blockOpen;
        /// <summary>The number of blocks indexed so far.</summary>
        private int _blockCount;
        /// <summary>The digest, once the end of the file was reached.</summary>
        private FileDigest? _digest;

        /// <summary>Creates a stream over an open file.</summary>
        /// <param name="store">The store that receives the indexed blocks.</param>
        /// <param name="file">The index entry of the file.</param>
        /// <param name="inner">The file, positioned at its beginning.</param>
        /// <param name="blockSize">The block size of the content geometry.</param>
        internal IndexingFileStream(FileBlockStore store, FileBlockStore.IndexedFile file, FileStream inner, int blockSize)
        {
            _store = store;
            _file = file;
            _inner = inner;
            _blockSize = blockSize;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _inner.Length;

        /// <summary>Gets the number of bytes read so far; the stream cannot be repositioned.</summary>
        /// <exception cref="NotSupportedException">The position is set.</exception>
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException("The indexing stream is forward-only.");
        }

        /// <summary>
        /// Reads the rest of the file, if the consumer stopped before its end, and returns the complete-file digest. After it
        /// returns, every block of the file is indexed.
        /// </summary>
        /// <param name="cancellationToken">Cancels reading.</param>
        /// <returns>The complete-file hash and length.</returns>
        /// <exception cref="IOException">The file cannot be read.</exception>
        /// <exception cref="OperationCanceledException">Reading was cancelled.</exception>
        public async ValueTask<FileDigest> CompleteAsync(CancellationToken cancellationToken)
        {
            if (_digest == null)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
                try
                {
                    while (await ReadAsync(buffer.AsMemory(0, _blockSize), cancellationToken).ConfigureAwait(false) != 0)
                    {
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            return _digest!.Value;
        }

        /// <summary>Copies the leaf identifiers of every block, in file order, for persistence.</summary>
        /// <returns>The contiguous 32-byte identifiers.</returns>
        /// <exception cref="InvalidOperationException">The file has not been read to its end.</exception>
        public byte[] ExportLeaves()
        {
            if (_digest == null)
            {
                throw new InvalidOperationException("The leaves are complete only after the file was read to its end.");
            }
            var leaves = new byte[_leaves.Count * LeafId.Length];
            for (var index = 0; index < _leaves.Count; index++)
            {
                _leaves[index].WriteTo(leaves.AsSpan(index * LeafId.Length));
            }
            return leaves;
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Consume(buffer.Span[..count]);
            return count;
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        /// <inheritdoc />
        public override int Read(Span<byte> buffer)
        {
            var count = _inner.Read(buffer);
            Consume(buffer[..count]);
            return count;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        /// <exception cref="NotSupportedException">Always.</exception>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException("The indexing stream is forward-only.");
        }

        /// <inheritdoc />
        /// <exception cref="NotSupportedException">Always.</exception>
        public override void SetLength(long value)
        {
            throw new NotSupportedException("The indexing stream is read-only.");
        }

        /// <inheritdoc />
        /// <exception cref="NotSupportedException">Always.</exception>
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("The indexing stream is read-only.");
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            DisposeHashes();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                DisposeHashes();
            }
            base.Dispose(disposing);
        }

        /// <summary>Feeds bytes that were handed to the consumer into the file and block hashes, indexing each block it completes.</summary>
        /// <param name="data">The bytes just read; empty at the end of the file.</param>
        private void Consume(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                Complete();
                return;
            }
            _fileHash.AppendData(data);
            while (!data.IsEmpty)
            {
                OpenBlockIfNeeded();
                var take = Math.Min(_blockSize - _blockFill, data.Length);
                _blockHash.AppendData(data[..take]);
                _blockFill += take;
                _position += take;
                data = data[take..];
                if (_blockFill == _blockSize)
                {
                    CloseBlock();
                }
            }
        }

        /// <summary>Starts the leaf hash of a block with the domain separation byte.</summary>
        private void OpenBlockIfNeeded()
        {
            if (!_blockOpen)
            {
                _blockHash.AppendData(s_leafPrefix);
                _blockOpen = true;
            }
        }

        /// <summary>Finishes the leaf hash of the current block and indexes the block.</summary>
        private void CloseBlock()
        {
            Span<byte> digest = stackalloc byte[LeafId.Length];
            _blockHash.GetHashAndReset(digest);
            var leafId = LeafId.FromDigest(digest);
            _leaves.Add(leafId);
            _store.AddBlock(_file, leafId, _position - _blockFill, _blockFill);
            _blockCount++;
            _blockFill = 0;
            _blockOpen = false;
        }

        /// <summary>Indexes the trailing partial block, or the single empty block of an empty file, and fixes the digest.</summary>
        private void Complete()
        {
            if (_digest != null)
            {
                return;
            }
            if (_blockOpen || _blockCount == 0)
            {
                OpenBlockIfNeeded();
                CloseBlock();
            }
            Span<byte> digest = stackalloc byte[LeafId.Length];
            _fileHash.GetHashAndReset(digest);
            _digest = new FileDigest(Convert.ToHexStringLower(digest), _position);
        }

        /// <summary>Releases the hash states.</summary>
        private void DisposeHashes()
        {
            _fileHash.Dispose();
            _blockHash.Dispose();
        }
    }
}
