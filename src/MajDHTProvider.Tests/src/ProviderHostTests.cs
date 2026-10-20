using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProvider.Charts;
using MajDHTProvider.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>Runs a publisher host and a replica host end to end, driven only by the file system and a DHT peer.</summary>
    public sealed class ProviderHostTests
    {
        /// <summary>
        /// Publishes a chart dropped into the publisher directory, pulls it into the replica on a peer request, and deletes
        /// the replica copy after the chart directory is removed from the publisher.
        /// </summary>
        /// <returns>Completion after both hosts stopped.</returns>
        [Fact]
        public async Task PublisherAndReplicaHostsFollowTheChartDirectories()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var publisherRoot = directory.Combine("publisher");
            var replicaRoot = directory.Combine("replica");
            var publisherPort = FreeTcpPort();
            var replicaPort = FreeTcpPort();
            var publisherOptions = CommandLineParser.Parse(
            [
                "publisher", "--root", publisherRoot, "--listen", "127.0.0.1", "--tcp-port", publisherPort.ToString(),
                "--udp-port", "none", "--work-bits", "0", "--quiet-period", "200ms"
            ])!;
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
            var publisherRun = new ProviderHost(publisherOptions, NullLoggerFactory.Instance).RunAsync(stopping.Token);
            var publisherId = await ReadNodeIdAsync(publisherOptions, token);
            var replicaOptions = CommandLineParser.Parse(
            [
                "replica", "--root", replicaRoot, "--publisher", publisherId, "--listen", "127.0.0.1",
                "--tcp-port", replicaPort.ToString(), "--udp-port", "none", "--work-bits", "0",
                "--bootstrap", $"127.0.0.1:{publisherPort}", "--quiet-period", "200ms", "--refresh-interval", "1s", "--pull-wait", "4s"
            ])!;
            var replicaRun = new ProviderHost(replicaOptions, NullLoggerFactory.Instance).RunAsync(stopping.Token);
            var replicaId = await ReadNodeIdAsync(replicaOptions, token);
            var readerOptions = TestSupport.NodeOptions(DHTCapabilities.Download);
            readerOptions.BootstrapEndpoints.Add(new IPEndPoint(IPAddress.Loopback, publisherPort));
            readerOptions.BootstrapEndpoints.Add(new IPEndPoint(IPAddress.Loopback, replicaPort));
            await using var reader = new MajDHTNode(readerOptions);
            await reader.StartAsync(token);
            await TestSupport.WaitUntilAsync(async () => await reader.BootstrapAsync(token) == 2, token);

            var resource = new ChartResource(Guid.NewGuid(), ChartResourceKind.Maidata);
            var content = TestSupport.WriteResource(publisherRoot, resource, 30000);
            var key = new DHTKey(publisherId, resource.KeyName);
            DHTRecord? record = null;
            await TestSupport.WaitUntilAsync(async () => (record = await reader.ResolveAsync(key, token)) != null, token);
            var replicaPeer = reader.KnownPeers.Single(peer => peer.NodeId == replicaId);
            await TestSupport.WaitUntilAsync(async () => await TryReadBlockAsync(reader, record!, replicaPeer, token), token);
            var replicaFile = TestSupport.ResourcePath(replicaRoot, resource);
            await TestSupport.WaitUntilAsync(() => Task.FromResult(File.Exists(replicaFile)), token);
            TestSupport.AssertBytes(content, await File.ReadAllBytesAsync(replicaFile, token));

            Directory.Delete(Path.Combine(publisherRoot, ChartResource.FormatChartId(resource.ChartId)), true);
            await TestSupport.WaitUntilAsync(async () => (await reader.ResolveAsync(key, token))?.IsWithdrawn == true, token);
            await TestSupport.WaitUntilAsync(() => Task.FromResult(!File.Exists(replicaFile)), token);

            await stopping.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisherRun);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replicaRun);
        }

        /// <summary>
        /// Saves the routing table when a host stops and rejoins through it at the next start: a replica restarted on a new
        /// port without any bootstrap endpoint is found through the publisher and still pulls from the origin.
        /// </summary>
        /// <returns>Completion after both hosts stopped.</returns>
        [Fact]
        public async Task ReplicaRejoinsThroughTheSavedRoutingTable()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var publisherRoot = directory.Combine("publisher");
            var replicaRoot = directory.Combine("replica");
            var publisherPort = FreeTcpPort();
            var publisherOptions = CommandLineParser.Parse(
            [
                "publisher", "--root", publisherRoot, "--listen", "127.0.0.1", "--tcp-port", publisherPort.ToString(),
                "--udp-port", "none", "--work-bits", "0", "--quiet-period", "200ms"
            ])!;
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
            var publisherRun = new ProviderHost(publisherOptions, NullLoggerFactory.Instance).RunAsync(stopping.Token);
            var publisherId = await ReadNodeIdAsync(publisherOptions, token);

            // The first run joins through the bootstrap endpoint and leaves a routing table behind when it stops.
            var firstOptions = CommandLineParser.Parse(
            [
                "replica", "--root", replicaRoot, "--publisher", publisherId, "--listen", "127.0.0.1",
                "--tcp-port", FreeTcpPort().ToString(), "--udp-port", "none", "--work-bits", "0",
                "--bootstrap", $"127.0.0.1:{publisherPort}", "--quiet-period", "200ms"
            ])!;
            var routingPath = Path.Combine(firstOptions.GetDataDirectory(), RoutingTableFile.FileName);
            using (var firstStop = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var firstRun = new ProviderHost(firstOptions, NullLoggerFactory.Instance).RunAsync(firstStop.Token);
                await TestSupport.WaitUntilAsync(() => Task.FromResult(File.Exists(routingPath)), token);
                await firstStop.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRun);
            }
            var replicaId = await ReadNodeIdAsync(firstOptions, token);

            // The second run has no bootstrap endpoint and a new port, so only the restored contact can tell the publisher about it.
            var secondPort = FreeTcpPort();
            var secondOptions = CommandLineParser.Parse(
            [
                "replica", "--root", replicaRoot, "--publisher", publisherId, "--listen", "127.0.0.1",
                "--tcp-port", secondPort.ToString(), "--udp-port", "none", "--work-bits", "0",
                "--quiet-period", "200ms", "--pull-wait", "4s"
            ])!;
            var secondRun = new ProviderHost(secondOptions, NullLoggerFactory.Instance).RunAsync(stopping.Token);
            var readerOptions = TestSupport.NodeOptions(DHTCapabilities.Download);
            readerOptions.BootstrapEndpoints.Add(new IPEndPoint(IPAddress.Loopback, publisherPort));
            await using var reader = new MajDHTNode(readerOptions);
            await reader.StartAsync(token);
            DHTPeer? replicaPeer = null;
            await TestSupport.WaitUntilAsync(async () =>
            {
                await reader.BootstrapAsync(token);
                replicaPeer = reader.KnownPeers.SingleOrDefault(peer => peer.NodeId == replicaId && peer.Endpoints[0].TcpPort == secondPort);
                return replicaPeer != null;
            }, token);

            var resource = new ChartResource(Guid.NewGuid(), ChartResourceKind.Maidata);
            var content = TestSupport.WriteResource(publisherRoot, resource, 30000);
            var key = new DHTKey(publisherId, resource.KeyName);
            DHTRecord? record = null;
            await TestSupport.WaitUntilAsync(async () => (record = await reader.ResolveAsync(key, token)) != null, token);
            await TestSupport.WaitUntilAsync(async () => await TryReadBlockAsync(reader, record!, replicaPeer!, token), token);
            var replicaFile = TestSupport.ResourcePath(replicaRoot, resource);
            await TestSupport.WaitUntilAsync(() => Task.FromResult(File.Exists(replicaFile)), token);
            TestSupport.AssertBytes(content, await File.ReadAllBytesAsync(replicaFile, token));

            await stopping.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisherRun);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondRun);
            Assert.True(File.Exists(Path.Combine(publisherOptions.GetDataDirectory(), RoutingTableFile.FileName)));
        }

        /// <summary>Reads the node ID from a host's identity file once the host created it.</summary>
        /// <param name="options">The host options.</param>
        /// <param name="cancellationToken">The test deadline.</param>
        /// <returns>The node ID.</returns>
        private static async Task<string> ReadNodeIdAsync(ProviderOptions options, CancellationToken cancellationToken)
        {
            var path = Path.Combine(options.GetDataDirectory(), IdentityFile.FileName);
            await TestSupport.WaitUntilAsync(() => Task.FromResult(File.Exists(path)), cancellationToken);
            return new DHTIdentity(await File.ReadAllBytesAsync(path, cancellationToken)).NodeId;
        }

        /// <summary>Requests the first block of a record from a specific provider.</summary>
        /// <param name="reader">The requesting node.</param>
        /// <param name="record">The record.</param>
        /// <param name="provider">The provider.</param>
        /// <param name="cancellationToken">The test deadline.</param>
        /// <returns>Whether the provider served the block.</returns>
        private static async Task<bool> TryReadBlockAsync(MajDHTNode reader, DHTRecord record, DHTPeer provider, CancellationToken cancellationToken)
        {
            try
            {
                await reader.ReadBlockAsync(record, provider, 0, cancellationToken: cancellationToken);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>Finds a currently unused loopback TCP port.</summary>
        /// <returns>The port.</returns>
        private static int FreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
