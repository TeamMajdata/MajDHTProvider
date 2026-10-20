using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProvider.Charts;
using MajDHTProvider.Publishing;
using Xunit;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>Checks publisher mode against real loopback DHT nodes.</summary>
    public sealed class ChartPublisherTests
    {
        /// <summary>Publishes every chart resource on disk and nothing outside the layout.</summary>
        /// <returns>Completion after the downloads.</returns>
        [Fact]
        public async Task InitialSynchronizationPublishesTheChartLayout()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var chartId = Guid.NewGuid();
            var maidata = new ChartResource(chartId, ChartResourceKind.Maidata);
            var image = new ChartResource(chartId, ChartResourceKind.ImageFullSize);
            var maidataBytes = TestSupport.WriteResource(directory.Combine("root"), maidata, 40000);
            var imageBytes = TestSupport.WriteResource(directory.Combine("root"), image, 0);
            File.WriteAllText(directory.Combine("root", ChartResource.FormatChartId(chartId), "notes.txt"), "not published");
            await using var publisher = await PublisherHarness.StartAsync(directory, new DHTIdentity(), TimeSpan.FromHours(1), token);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, publisher.Node)), token);

            await publisher.Publisher.SynchronizeAllAsync(token);

            TestSupport.AssertBytes(maidataBytes, await TestSupport.DownloadAsync(reader, publisher.Node.NodeId, maidata, token));
            TestSupport.AssertBytes(imageBytes, await TestSupport.DownloadAsync(reader, publisher.Node.NodeId, image, token));
            var video = new ChartResource(chartId, ChartResourceKind.Video);
            Assert.Null(await reader.ResolveAsync(new DHTKey(publisher.Node.NodeId, video.KeyName), token));
            var ledger = PublicationLedger.Load(publisher.LedgerPath);
            Assert.Equal(2, ledger.PublishedKeyNames.Count);
            Assert.Equal(2, publisher.Store.IndexedFileCount);
        }

        /// <summary>Republishes changed files and withdraws removed files and chart directories.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task ChangesAreRepublishedAndRemovalsWithdrawn()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var root = directory.Combine("root");
            var chartId = Guid.NewGuid();
            var maidata = new ChartResource(chartId, ChartResourceKind.Maidata);
            var track = new ChartResource(chartId, ChartResourceKind.Track);
            TestSupport.WriteResource(root, maidata, 1000);
            TestSupport.WriteResource(root, track, 20000);
            await using var publisher = await PublisherHarness.StartAsync(directory, new DHTIdentity(), TimeSpan.FromHours(1), token);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, publisher.Node)), token);
            await publisher.Publisher.SynchronizeAllAsync(token);
            var maidataKey = new DHTKey(publisher.Node.NodeId, maidata.KeyName);
            var first = (await reader.ResolveAsync(maidataKey, token))!;

            await publisher.Publisher.SynchronizeChartAsync(chartId, token);
            Assert.Equal(first.Revision, (await reader.ResolveAsync(maidataKey, token))!.Revision);

            var changed = TestSupport.WriteResource(root, maidata, 1500);
            await publisher.Publisher.SynchronizeChartAsync(chartId, token);
            var second = (await reader.ResolveAsync(maidataKey, token))!;
            Assert.True(second.Revision > first.Revision);
            TestSupport.AssertBytes(changed, await TestSupport.DownloadAsync(reader, publisher.Node.NodeId, maidata, token));

            File.Delete(TestSupport.ResourcePath(root, track));
            await publisher.Publisher.SynchronizeChartAsync(chartId, token);
            Assert.True((await reader.ResolveAsync(new DHTKey(publisher.Node.NodeId, track.KeyName), token))!.IsWithdrawn);
            Assert.False((await reader.ResolveAsync(maidataKey, token))!.IsWithdrawn);

            Directory.Delete(Path.Combine(root, ChartResource.FormatChartId(chartId)), true);
            await publisher.Publisher.SynchronizeChartAsync(chartId, token);
            Assert.True((await reader.ResolveAsync(maidataKey, token))!.IsWithdrawn);
            Assert.Empty(PublicationLedger.Load(publisher.LedgerPath).PublishedKeyNames);
            Assert.Equal(0, publisher.Store.IndexedFileCount);
        }

        /// <summary>Withdraws, at the next start, resources removed while the publisher was offline, and keeps serving the others.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task RestartWithdrawsResourcesRemovedWhileOffline()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var root = directory.Combine("root");
            var identity = new DHTIdentity();
            var kept = new ChartResource(Guid.NewGuid(), ChartResourceKind.Maidata);
            var removed = new ChartResource(Guid.NewGuid(), ChartResourceKind.Maidata);
            var keptBytes = TestSupport.WriteResource(root, kept, 100);
            TestSupport.WriteResource(root, removed, 100);
            long firstRevision;
            await using (var first = await PublisherHarness.StartAsync(directory, identity, TimeSpan.FromHours(1), token))
            {
                await first.Publisher.SynchronizeAllAsync(token);
                Assert.True(PublicationLedger.Load(first.LedgerPath).TryGet(kept.KeyName, out var entry));
                firstRevision = entry.Revision;
            }
            Directory.Delete(Path.Combine(root, ChartResource.FormatChartId(removed.ChartId)), true);

            await using var second = await PublisherHarness.StartAsync(directory, identity, TimeSpan.FromHours(1), token);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, second.Node)), token);
            await second.Publisher.SynchronizeAllAsync(token);

            Assert.True((await reader.ResolveAsync(new DHTKey(identity.NodeId, removed.KeyName), token))!.IsWithdrawn);
            // The kept resource is resumed from its cached leaves under the revision it already had.
            var resumed = (await reader.ResolveAsync(new DHTKey(identity.NodeId, kept.KeyName), token))!;
            Assert.Equal(firstRevision, resumed.Revision);
            TestSupport.AssertBytes(keptBytes, await TestSupport.DownloadAsync(reader, identity.NodeId, kept, token));
        }

        /// <summary>Re-signs publications with a higher revision once half of their lifetime has passed.</summary>
        /// <returns>Completion after the renewal.</returns>
        [Fact]
        public async Task PublicationsAreRenewedAtHalfLife()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var resource = new ChartResource(Guid.NewGuid(), ChartResourceKind.Video);
            var content = TestSupport.WriteResource(directory.Combine("root"), resource, 30000);
            await using var publisher = await PublisherHarness.StartAsync(directory, new DHTIdentity(), TimeSpan.FromSeconds(4), token);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, publisher.Node)), token);
            await publisher.Publisher.SynchronizeAllAsync(token);
            var key = new DHTKey(publisher.Node.NodeId, resource.KeyName);
            var original = (await reader.ResolveAsync(key, token))!;
            Assert.Equal(0, await publisher.Publisher.RenewDueAsync(token));

            await Task.Delay(original.ExpiresAt - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1.5), token);
            Assert.Equal(1, await publisher.Publisher.RenewDueAsync(token));

            var renewed = (await reader.ResolveAsync(key, token))!;
            Assert.True(renewed.Revision > original.Revision);
            Assert.True(renewed.ExpiresAt > original.ExpiresAt);
            Assert.Equal(original.FileHash, renewed.FileHash);
            TestSupport.AssertBytes(content, await TestSupport.DownloadAsync(reader, publisher.Node.NodeId, resource, token));
        }

        /// <summary>
        /// Publishes, renews and withdraws many resources several at a time: every resource gets its own publication, an
        /// unchanged rescan does not rewrite the ledger, every publication is renewed together at half-life, and the
        /// resources of removed charts are withdrawn together.
        /// </summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task ManyResourcesArePublishedRenewedAndWithdrawnTogether()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var root = directory.Combine("root");
            var chartIds = new List<Guid>();
            var resources = new List<ChartResource>();
            for (var chart = 0; chart < 12; chart++)
            {
                var chartId = Guid.NewGuid();
                chartIds.Add(chartId);
                foreach (var kind in ChartResource.AllKinds)
                {
                    var resource = new ChartResource(chartId, kind);
                    resources.Add(resource);
                    TestSupport.WriteResource(root, resource, 20000 + resources.Count);
                }
            }
            var lifetime = TimeSpan.FromSeconds(8);
            await using var publisher = await PublisherHarness.StartAsync(directory, new DHTIdentity(), lifetime, token, parallelism: 8);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, publisher.Node)), token);

            var stopwatch = Stopwatch.StartNew();
            await publisher.Publisher.SynchronizeAllAsync(token);
            var published = DateTimeOffset.UtcNow;
            TestContext.Current.TestOutputHelper?.WriteLine($"Published {resources.Count} resources in {stopwatch.Elapsed.TotalSeconds:F2}s.");

            Assert.Equal(resources.Count, PublicationLedger.Load(publisher.LedgerPath).PublishedKeyNames.Count);
            Assert.Equal(resources.Count, publisher.Store.IndexedFileCount);
            var records = new List<DHTRecord>();
            foreach (var resource in resources)
            {
                var record = await reader.ResolveAsync(new DHTKey(publisher.Node.NodeId, resource.KeyName), token);
                Assert.NotNull(record);
                records.Add(record);
            }
            var ledgerWritten = File.GetLastWriteTimeUtc(publisher.LedgerPath);
            await publisher.Publisher.SynchronizeAllAsync(token);
            Assert.Equal(ledgerWritten, File.GetLastWriteTimeUtc(publisher.LedgerPath));
            Assert.Equal(0, await publisher.Publisher.RenewDueAsync(token));

            // Every record was signed before the synchronization ended, so all reach half-life within half a lifetime of it.
            var untilDue = published + (lifetime / 2) + TimeSpan.FromMilliseconds(200) - DateTimeOffset.UtcNow;
            if (untilDue > TimeSpan.Zero)
            {
                await Task.Delay(untilDue, token);
            }
            Assert.Equal(resources.Count, await publisher.Publisher.RenewDueAsync(token));
            for (var index = 0; index < resources.Count; index++)
            {
                var renewed = await reader.ResolveAsync(records[index].Key, token);
                Assert.NotNull(renewed);
                Assert.True(renewed.Revision > records[index].Revision);
                Assert.Equal(records[index].FileHash, renewed.FileHash);
            }

            var removedCharts = chartIds.GetRange(0, 6);
            foreach (var chartId in removedCharts)
            {
                Directory.Delete(Path.Combine(root, ChartResource.FormatChartId(chartId)), true);
            }
            await publisher.Publisher.SynchronizeAllAsync(token);
            var remaining = resources.Count - (removedCharts.Count * ChartResource.AllKinds.Count);
            Assert.Equal(remaining, PublicationLedger.Load(publisher.LedgerPath).PublishedKeyNames.Count);
            Assert.Equal(remaining, publisher.Store.IndexedFileCount);
            foreach (var resource in resources)
            {
                var record = await reader.ResolveAsync(new DHTKey(publisher.Node.NodeId, resource.KeyName), token);
                Assert.NotNull(record);
                Assert.Equal(removedCharts.Contains(resource.ChartId), record.IsWithdrawn);
            }
        }

        /// <summary>
        /// Resumes unchanged resources after a restart from their cached leaves without reading the files: a record with
        /// more than half of its lifetime left keeps its revision, a file whose content changed under an identical stamp
        /// is trusted as unchanged, and a file whose stamp changed is read and published again.
        /// </summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task RestartResumesUnchangedResourcesFromCachedLeaves()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var root = directory.Combine("root");
            var identity = new DHTIdentity();
            var chartId = Guid.NewGuid();
            var kept = new ChartResource(chartId, ChartResourceKind.Maidata);
            var tampered = new ChartResource(chartId, ChartResourceKind.Image);
            var changed = new ChartResource(chartId, ChartResourceKind.Track);
            var keptBytes = TestSupport.WriteResource(root, kept, 40000);
            TestSupport.WriteResource(root, tampered, 20000);
            TestSupport.WriteResource(root, changed, 30000);
            var records = new Dictionary<ChartResource, DHTRecord>();
            await using (var first = await PublisherHarness.StartAsync(directory, identity, TimeSpan.FromHours(1), token))
            {
                await using var firstReader = await TestSupport.StartAsync(
                    new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, first.Node)), token);
                await first.Publisher.SynchronizeAllAsync(token);
                foreach (var resource in new[] { kept, tampered, changed })
                {
                    records[resource] = (await firstReader.ResolveAsync(new DHTKey(identity.NodeId, resource.KeyName), token))!;
                }
            }
            // Same length and write time but other content: the stamp says unchanged, so the cached leaves are trusted.
            var tamperedPath = TestSupport.ResourcePath(root, tampered);
            var tamperedStamp = Storage.FileStamp.Read(tamperedPath);
            await File.WriteAllBytesAsync(tamperedPath, RandomNumberGenerator.GetBytes(20000), token);
            File.SetLastWriteTimeUtc(tamperedPath, tamperedStamp.LastWriteTimeUtc);
            Assert.Equal(tamperedStamp, Storage.FileStamp.Read(tamperedPath));
            var changedBytes = TestSupport.WriteResource(root, changed, 30001);

            await using var second = await PublisherHarness.StartAsync(directory, identity, TimeSpan.FromHours(1), token);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, second.Node)), token);
            await second.Publisher.SynchronizeAllAsync(token);

            var keptAgain = (await reader.ResolveAsync(records[kept].Key, token))!;
            Assert.Equal(records[kept].Revision, keptAgain.Revision);
            TestSupport.AssertBytes(keptBytes, await TestSupport.DownloadAsync(reader, identity.NodeId, kept, token));
            var tamperedAgain = (await reader.ResolveAsync(records[tampered].Key, token))!;
            Assert.Equal(records[tampered].Revision, tamperedAgain.Revision);
            Assert.Equal(records[tampered].FileHash, tamperedAgain.FileHash);
            var changedAgain = (await reader.ResolveAsync(records[changed].Key, token))!;
            Assert.True(changedAgain.Revision > records[changed].Revision);
            Assert.NotEqual(records[changed].FileHash, changedAgain.FileHash);
            TestSupport.AssertBytes(changedBytes, await TestSupport.DownloadAsync(reader, identity.NodeId, changed, token));
            Assert.Equal(3, second.Store.IndexedFileCount);
        }

        /// <summary>Signs a fresh revision from the cached leaves, still without reading the file, once the cached record passed half of its lifetime.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task RestartRenewsExpiringResourcesFromCachedLeaves()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var root = directory.Combine("root");
            var identity = new DHTIdentity();
            var resource = new ChartResource(Guid.NewGuid(), ChartResourceKind.Video);
            TestSupport.WriteResource(root, resource, 50000);
            var lifetime = TimeSpan.FromSeconds(4);
            DHTRecord original;
            await using (var first = await PublisherHarness.StartAsync(directory, identity, lifetime, token))
            {
                await using var firstReader = await TestSupport.StartAsync(
                    new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, first.Node)), token);
                await first.Publisher.SynchronizeAllAsync(token);
                original = (await firstReader.ResolveAsync(new DHTKey(identity.NodeId, resource.KeyName), token))!;
            }
            var path = TestSupport.ResourcePath(root, resource);
            var stamp = Storage.FileStamp.Read(path);
            await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(50000), token);
            File.SetLastWriteTimeUtc(path, stamp.LastWriteTimeUtc);
            await Task.Delay(lifetime / 2, token);

            await using var second = await PublisherHarness.StartAsync(directory, identity, lifetime, token);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, second.Node)), token);
            await second.Publisher.SynchronizeAllAsync(token);

            var renewed = (await reader.ResolveAsync(original.Key, token))!;
            Assert.True(renewed.Revision > original.Revision);
            Assert.True(renewed.ExpiresAt > original.ExpiresAt);
            Assert.Equal(original.FileHash, renewed.FileHash);
            Assert.Equal(1, second.Store.IndexedFileCount);
        }

        /// <summary>Creates the per-test deadline.</summary>
        /// <returns>A token source cancelled after 60 seconds or when the test is aborted.</returns>
        private static CancellationTokenSource Deadline()
        {
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            return deadline;
        }
    }
}
