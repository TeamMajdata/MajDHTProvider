using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProvider.Charts;
using Xunit;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>Checks replica mode against a real loopback publisher.</summary>
    public sealed class ChartReplicaTests
    {
        /// <summary>Pulls a requested resource from the origin, stores it and then serves it without the origin.</summary>
        /// <returns>Completion after the downloads.</returns>
        [Fact]
        public async Task PeerRequestsPullMissingResourcesFromTheOrigin()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var resource = new ChartResource(Guid.NewGuid(), ChartResourceKind.Track);
            var content = TestSupport.WriteResource(directory.Combine("root"), resource, (16384 * 3) + 7);
            await using var publisher = await PublisherHarness.StartAsync(directory, new DHTIdentity(), TimeSpan.FromHours(1), token);
            await publisher.Publisher.SynchronizeAllAsync(token);
            await using var replica = await ReplicaHarness.StartAsync(directory.Combine("replica"), publisher.Node.NodeId, publisher.Node, token);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, publisher.Node)), token);
            var record = (await reader.ResolveAsync(new DHTKey(publisher.Node.NodeId, resource.KeyName), token))!;
            Assert.False(File.Exists(TestSupport.ResourcePath(replica.Root, resource)));

            var block = await reader.ReadBlockAsync(record, replica.Node.LocalPeer, 1, cancellationToken: token);

            TestSupport.AssertBytes(content.AsMemory(16384, 16384), block);
            await replica.Replica.WhenPullsCompleteAsync();
            TestSupport.AssertBytes(content, await File.ReadAllBytesAsync(TestSupport.ResourcePath(replica.Root, resource), token));
            Assert.Equal(1, replica.Replica.ServedCount);
            Assert.True(publisher.Node.StopProviding(record.FileHash));
            TestSupport.AssertBytes(content, await TestSupport.DownloadAsync(reader, publisher.Node.NodeId, resource, token));
            var direct = await reader.ReadBlockAsync(record, replica.Node.LocalPeer, 3, cancellationToken: token);
            TestSupport.AssertBytes(content.AsMemory(16384 * 3), direct);
        }

        /// <summary>Serves matching local files without pulling, replaces outdated ones and leaves unpublished ones alone.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task LocalFilesAreServedReplacedOrLeftAlone()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var chartId = Guid.NewGuid();
            var maidata = new ChartResource(chartId, ChartResourceKind.Maidata);
            var image = new ChartResource(chartId, ChartResourceKind.Image);
            var video = new ChartResource(chartId, ChartResourceKind.Video);
            var maidataBytes = TestSupport.WriteResource(directory.Combine("root"), maidata, 20000);
            var imageBytes = TestSupport.WriteResource(directory.Combine("root"), image, 5000);
            await using var publisher = await PublisherHarness.StartAsync(directory, new DHTIdentity(), TimeSpan.FromHours(1), token);
            await publisher.Publisher.SynchronizeAllAsync(token);
            var replicaRoot = directory.Combine("replica");
            TestSupport.WriteResource(replicaRoot, maidata, maidataBytes);
            TestSupport.WriteResource(replicaRoot, image, 4000);
            var videoBytes = TestSupport.WriteResource(replicaRoot, video, 3000);
            await using var replica = await ReplicaHarness.StartAsync(replicaRoot, publisher.Node.NodeId, publisher.Node, token);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, publisher.Node)), token);

            await replica.Replica.SynchronizeAllAsync(token);
            await replica.Replica.WhenPullsCompleteAsync();

            Assert.Equal(2, replica.Replica.ServedCount);
            TestSupport.AssertBytes(imageBytes, await File.ReadAllBytesAsync(TestSupport.ResourcePath(replicaRoot, image), token));
            TestSupport.AssertBytes(videoBytes, await File.ReadAllBytesAsync(TestSupport.ResourcePath(replicaRoot, video), token));
            var record = (await reader.ResolveAsync(new DHTKey(publisher.Node.NodeId, maidata.KeyName), token))!;
            Assert.True(publisher.Node.StopProviding(record.FileHash));
            TestSupport.AssertBytes(maidataBytes.AsMemory(0, 16384),
                await reader.ReadBlockAsync(record, replica.Node.LocalPeer, 0, cancellationToken: token));
        }

        /// <summary>Deletes local files only after a withdrawal; a file removed by hand is merely no longer served.</summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task OnlyWithdrawalsDeleteLocalFiles()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var publisherRoot = directory.Combine("root");
            var replicaRoot = directory.Combine("replica");
            var chartId = Guid.NewGuid();
            var maidata = new ChartResource(chartId, ChartResourceKind.Maidata);
            var image = new ChartResource(chartId, ChartResourceKind.Image);
            TestSupport.WriteResource(replicaRoot, maidata, TestSupport.WriteResource(publisherRoot, maidata, 700));
            TestSupport.WriteResource(replicaRoot, image, TestSupport.WriteResource(publisherRoot, image, 800));
            await using var publisher = await PublisherHarness.StartAsync(directory, new DHTIdentity(), TimeSpan.FromHours(1), token);
            await publisher.Publisher.SynchronizeAllAsync(token);
            await using var replica = await ReplicaHarness.StartAsync(replicaRoot, publisher.Node.NodeId, publisher.Node, token);
            await replica.Replica.SynchronizeAllAsync(token);
            Assert.Equal(2, replica.Replica.ServedCount);

            File.Delete(TestSupport.ResourcePath(replicaRoot, image));
            await replica.Replica.SynchronizeChartAsync(chartId, token);
            Assert.Equal(1, replica.Replica.ServedCount);
            await replica.Replica.RefreshAsync(token);
            Assert.True(File.Exists(TestSupport.ResourcePath(replicaRoot, maidata)), "A refresh without withdrawals must keep files.");

            File.Delete(TestSupport.ResourcePath(publisherRoot, maidata));
            await publisher.Publisher.SynchronizeChartAsync(chartId, token);
            await replica.Replica.RefreshAsync(token);

            Assert.False(File.Exists(TestSupport.ResourcePath(replicaRoot, maidata)));
            Assert.False(Directory.Exists(Path.Combine(replicaRoot, ChartResource.FormatChartId(chartId))));
            Assert.Equal(0, replica.Replica.ServedCount);
        }

        /// <summary>
        /// Adopts many local files several at a time: matching files are served, outdated files are replaced by pulls, and a
        /// refresh of every served resource keeps all of them.
        /// </summary>
        /// <returns>Completion after the checks.</returns>
        [Fact]
        public async Task ManyLocalFilesAreAdoptedTogether()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var publisherRoot = directory.Combine("root");
            var replicaRoot = directory.Combine("replica");
            var resources = new List<ChartResource>();
            var outdated = new List<ChartResource>();
            for (var chart = 0; chart < 8; chart++)
            {
                var chartId = Guid.NewGuid();
                foreach (var kind in ChartResource.AllKinds)
                {
                    var resource = new ChartResource(chartId, kind);
                    resources.Add(resource);
                    var content = TestSupport.WriteResource(publisherRoot, resource, 3000 + resources.Count);
                    if (resources.Count % 5 == 0)
                    {
                        outdated.Add(resource);
                        TestSupport.WriteResource(replicaRoot, resource, 1000);
                    }
                    else
                    {
                        TestSupport.WriteResource(replicaRoot, resource, content);
                    }
                }
            }
            await using var publisher = await PublisherHarness.StartAsync(directory, new DHTIdentity(), TimeSpan.FromHours(1), token, parallelism: 8);
            await publisher.Publisher.SynchronizeAllAsync(token);
            await using var replica = await ReplicaHarness.StartAsync(replicaRoot, publisher.Node.NodeId, publisher.Node, token, parallelism: 8);

            var stopwatch = Stopwatch.StartNew();
            await replica.Replica.SynchronizeAllAsync(token);
            TestContext.Current.TestOutputHelper?.WriteLine($"Adopted {resources.Count} local files in {stopwatch.Elapsed.TotalSeconds:F2}s.");
            await replica.Replica.WhenPullsCompleteAsync();

            Assert.Equal(resources.Count, replica.Replica.ServedCount);
            foreach (var resource in outdated)
            {
                TestSupport.AssertBytes(await File.ReadAllBytesAsync(TestSupport.ResourcePath(publisherRoot, resource), token),
                    await File.ReadAllBytesAsync(TestSupport.ResourcePath(replicaRoot, resource), token));
            }
            await replica.Replica.RefreshAsync(token);
            Assert.Equal(resources.Count, replica.Replica.ServedCount);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, publisher.Node)), token);
            foreach (var resource in new[] { resources[0], resources[^1], outdated[0] })
            {
                var record = await reader.ResolveAsync(new DHTKey(publisher.Node.NodeId, resource.KeyName), token);
                Assert.NotNull(record);
                Assert.True(publisher.Node.StopProviding(record.FileHash));
                TestSupport.AssertBytes(await File.ReadAllBytesAsync(TestSupport.ResourcePath(publisherRoot, resource), token),
                    await reader.ReadBlockAsync(record, replica.Node.LocalPeer, 0, cancellationToken: token));
            }
        }

        /// <summary>Serves cached files again after a restart without the origin, any lookup or any read, until the next refresh checks them.</summary>
        /// <returns>Completion after the blocks were read from the restarted replica.</returns>
        [Fact]
        public async Task RestartServesCachedFilesWithoutTheOrigin()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var publisherRoot = directory.Combine("root");
            var replicaRoot = directory.Combine("replica");
            var chartId = Guid.NewGuid();
            var resources = new[]
            {
                new ChartResource(chartId, ChartResourceKind.Maidata),
                new ChartResource(chartId, ChartResourceKind.Track),
                new ChartResource(chartId, ChartResourceKind.Image)
            };
            var contents = new Dictionary<ChartResource, byte[]>();
            foreach (var resource in resources)
            {
                contents[resource] = TestSupport.WriteResource(publisherRoot, resource, 20000 + contents.Count);
                TestSupport.WriteResource(replicaRoot, resource, contents[resource]);
            }
            await using var publisher = await PublisherHarness.StartAsync(directory, new DHTIdentity(), TimeSpan.FromHours(1), token);
            await publisher.Publisher.SynchronizeAllAsync(token);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, publisher.Node)), token);
            var records = new Dictionary<ChartResource, DHTRecord>();
            foreach (var resource in resources)
            {
                records[resource] = (await reader.ResolveAsync(new DHTKey(publisher.Node.NodeId, resource.KeyName), token))!;
            }
            await using (var first = await ReplicaHarness.StartAsync(replicaRoot, publisher.Node.NodeId, publisher.Node, token))
            {
                await first.Replica.SynchronizeAllAsync(token);
                Assert.Equal(resources.Length, first.Replica.ServedCount);
            }
            // The origin is gone, so only the cache can bring the files back.
            await publisher.DisposeAsync();

            await using var second = await ReplicaHarness.StartAsync(replicaRoot, publisher.Node.NodeId, null, token);
            await second.Replica.SynchronizeAllAsync(token);

            Assert.Equal(resources.Length, second.Replica.ServedCount);
            foreach (var resource in resources)
            {
                var block = await reader.ReadBlockAsync(records[resource], second.Node.LocalPeer, 1, cancellationToken: token);
                TestSupport.AssertBytes(contents[resource].AsMemory(16384), block);
            }
        }

        /// <summary>Declines requests for other publishers and for keys outside the chart namespace.</summary>
        /// <returns>Completion after the refused requests.</returns>
        [Fact]
        public async Task UntrustedRequestsAreDeclined()
        {
            using var deadline = Deadline();
            var token = deadline.Token;
            using var directory = new TestDirectory();
            await using var publisher = await PublisherHarness.StartAsync(directory, new DHTIdentity(), TimeSpan.FromHours(1), token);
            await using var stranger = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download | DHTCapabilities.Provide, publisher.Node)), token);
            await using var replica = await ReplicaHarness.StartAsync(directory.Combine("replica"), publisher.Node.NodeId, publisher.Node, token);
            await using var reader = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, publisher.Node)), token);
            var chart = new ChartResource(Guid.NewGuid(), ChartResourceKind.Maidata);
            var foreign = await stranger.PublishAsync(chart.KeyName, new MemoryStream(new byte[100]), cancellationToken: token);
            var unrelated = await publisher.Node.PublishAsync("songs/not-a-chart", new MemoryStream(new byte[100]), cancellationToken: token);

            await Assert.ThrowsAnyAsync<IOException>(() => reader.ReadBlockAsync(foreign, replica.Node.LocalPeer, 0, cancellationToken: token));
            await Assert.ThrowsAnyAsync<IOException>(() => reader.ReadBlockAsync(unrelated, replica.Node.LocalPeer, 0, cancellationToken: token));

            await replica.Replica.WhenPullsCompleteAsync();
            Assert.Equal(0, replica.Replica.ServedCount);
            Assert.False(Directory.Exists(Path.Combine(replica.Root, ChartResource.FormatChartId(chart.ChartId))));
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
