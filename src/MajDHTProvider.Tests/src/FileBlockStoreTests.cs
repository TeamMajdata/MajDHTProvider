using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProvider.Storage;
using Xunit;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>Checks that blocks are served from indexed files and never served stale.</summary>
    public sealed class FileBlockStoreTests
    {
        /// <summary>The block size used by the tests.</summary>
        private const int BlockSize = 1024;

        /// <summary>Indexes a multi-block file and reads every block back from disk.</summary>
        /// <returns>Completion after the reads.</returns>
        [Fact]
        public async Task IndexedFilesServeTheirBlocks()
        {
            using var directory = new TestDirectory();
            var path = directory.Combine("track.mp3");
            var content = RandomNumberGenerator.GetBytes((BlockSize * 2) + 10);
            await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
            var store = new FileBlockStore(1024 * 1024);

            var digest = await store.IndexFileAsync(path, BlockSize, TestContext.Current.CancellationToken);

            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), digest.FileHash);
            Assert.Equal(content.Length, digest.Length);
            Assert.Equal(1, store.IndexedFileCount);
            for (var offset = 0; offset < content.Length; offset += BlockSize)
            {
                var block = content.AsMemory(offset, Math.Min(BlockSize, content.Length - offset));
                var stored = await store.GetAsync(FileBlockStore.ComputeLeafId(block.Span), TestContext.Current.CancellationToken);
                Assert.NotNull(stored);
                TestSupport.AssertBytes(block, stored.Value);
            }
        }

        /// <summary>Indexes an empty file as one empty block, as the protocol requires.</summary>
        /// <returns>Completion after the read.</returns>
        [Fact]
        public async Task EmptyFilesHaveOneEmptyBlock()
        {
            using var directory = new TestDirectory();
            var path = directory.Combine("empty");
            await File.WriteAllBytesAsync(path, [], TestContext.Current.CancellationToken);
            var store = new FileBlockStore(1024 * 1024);
            var digest = await store.IndexFileAsync(path, BlockSize, TestContext.Current.CancellationToken);
            Assert.Equal(0, digest.Length);
            var stored = await store.GetAsync(FileBlockStore.ComputeLeafId([]), TestContext.Current.CancellationToken);
            Assert.NotNull(stored);
            Assert.Equal(0, stored.Value.Length);
        }

        /// <summary>Refuses blocks of a file that changed or disappeared after indexing.</summary>
        /// <returns>Completion after the reads.</returns>
        [Fact]
        public async Task ChangedOrRemovedFilesAreNotServed()
        {
            using var directory = new TestDirectory();
            var path = directory.Combine("bg.jpg");
            var content = RandomNumberGenerator.GetBytes(BlockSize);
            await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
            var store = new FileBlockStore(1024 * 1024);
            await store.IndexFileAsync(path, BlockSize, TestContext.Current.CancellationToken);
            var leafId = FileBlockStore.ComputeLeafId(content);

            await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(BlockSize), TestContext.Current.CancellationToken);
            Assert.Null(await store.GetAsync(leafId, TestContext.Current.CancellationToken));
            File.Delete(path);
            Assert.Null(await store.GetAsync(leafId, TestContext.Current.CancellationToken));
            Assert.True(store.Remove(path));
            Assert.False(store.Remove(path));
            Assert.Equal(0, store.IndexedFileCount);
        }

        /// <summary>Serves a block from another file that contains it when the first file changed.</summary>
        /// <returns>Completion after the read.</returns>
        [Fact]
        public async Task DuplicateBlocksFallBackToAnotherFile()
        {
            using var directory = new TestDirectory();
            var first = directory.Combine("first");
            var second = directory.Combine("second");
            var content = RandomNumberGenerator.GetBytes(BlockSize);
            await File.WriteAllBytesAsync(first, content, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(second, content, TestContext.Current.CancellationToken);
            var store = new FileBlockStore(1024 * 1024);
            await store.IndexFileAsync(first, BlockSize, TestContext.Current.CancellationToken);
            await store.IndexFileAsync(second, BlockSize, TestContext.Current.CancellationToken);
            File.Delete(first);
            var stored = await store.GetAsync(FileBlockStore.ComputeLeafId(content), TestContext.Current.CancellationToken);
            Assert.NotNull(stored);
            TestSupport.AssertBytes(content, stored.Value);
        }

        /// <summary>Caches blocks without a backing file and skips blocks already backed by one.</summary>
        /// <returns>Completion after the reads.</returns>
        [Fact]
        public async Task UnindexedBlocksAreCachedInMemory()
        {
            var store = new FileBlockStore(1024 * 1024);
            var block = RandomNumberGenerator.GetBytes(100);
            var leafId = FileBlockStore.ComputeLeafId(block);
            Assert.Null(await store.GetAsync(leafId, TestContext.Current.CancellationToken));
            await store.PutAsync(leafId, block, TestContext.Current.CancellationToken);
            var stored = await store.GetAsync(leafId, TestContext.Current.CancellationToken);
            Assert.NotNull(stored);
            TestSupport.AssertBytes(block, stored.Value);
        }

        /// <summary>Serves a published file from disk with leaf identifiers that match the node's Merkle leaves.</summary>
        /// <returns>Completion after the download.</returns>
        [Fact]
        public async Task NodeServesPublicationsFromIndexedFiles()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var path = directory.Combine("maidata.txt");
            var content = RandomNumberGenerator.GetBytes((16384 * 3) + 5);
            await File.WriteAllBytesAsync(path, content, token);
            // Indexing before publishing keeps every block out of the memory cache, so served blocks come from the file.
            var store = new FileBlockStore(32768);
            await using var publisher = new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download | DHTCapabilities.Provide),
                contentStore: store);
            await publisher.StartAsync(token);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, publisher)), token);
            var digest = await store.IndexFileAsync(path, 16384, token);
            DHTRecord record;
            await using (var source = File.OpenRead(path))
            {
                record = await publisher.PublishAsync("songs/from-disk", source, cancellationToken: token);
            }
            Assert.Equal(digest.FileHash, record.FileHash);
            using var destination = new MemoryStream();
            await reader.DownloadAsync(record.Key, destination, record.FileHash, token);
            TestSupport.AssertBytes(content, destination.ToArray());
        }
    }
}
