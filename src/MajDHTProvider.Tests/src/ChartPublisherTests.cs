using System;
using System.IO;
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

        /// <summary>Withdraws, at the next start, resources removed while the publisher was offline.</summary>
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
            var republished = (await reader.ResolveAsync(new DHTKey(identity.NodeId, kept.KeyName), token))!;
            Assert.True(republished.Revision > firstRevision);
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
