using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProvider.Charts;
using MajDHTProvider.Hosting;
using MajDHTProvider.Replication;
using MajDHTProvider.Storage;
using Microsoft.Extensions.Logging.Abstractions;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>A started replica node and its replica-mode components.</summary>
    internal sealed class ReplicaHarness : IAsyncDisposable
    {
        /// <summary>Creates a harness.</summary>
        /// <param name="root">The replica chart root.</param>
        /// <param name="node">The started replica node.</param>
        /// <param name="replica">The replica.</param>
        private ReplicaHarness(string root, MajDHTNode node, ChartReplica replica)
        {
            Root = root;
            Node = node;
            Replica = replica;
        }

        /// <summary>Gets the replica chart root.</summary>
        public string Root { get; }

        /// <summary>Gets the started replica node.</summary>
        public MajDHTNode Node { get; }

        /// <summary>Gets the replica.</summary>
        public ChartReplica Replica { get; }

        /// <summary>Starts a replica of a publisher that joins the network through a bootstrap node.</summary>
        /// <param name="root">The replica chart root.</param>
        /// <param name="publisherId">The trusted publisher fingerprint.</param>
        /// <param name="bootstrap">The started bootstrap node.</param>
        /// <param name="cancellationToken">The test deadline.</param>
        /// <param name="parallelism">How many resources the replica and its node handle at once.</param>
        /// <returns>The started harness.</returns>
        public static async Task<ReplicaHarness> StartAsync(string root, string publisherId, MajDHTNode bootstrap,
            CancellationToken cancellationToken, int parallelism = 4)
        {
            var charts = new ChartDirectory(root);
            var store = new FileBlockStore(1024 * 1024);
            var replica = new ChartReplica(charts, store, publisherId,
                Path.Combine(root, ProviderOptions.DefaultDataDirectoryName, ProviderHost.StagingDirectoryName),
                TimeSpan.FromSeconds(8), TimeSpan.FromHours(1), parallelism, NullLogger.Instance);
            var options = TestSupport.NodeOptions(DHTCapabilities.Download | DHTCapabilities.Provide, bootstrap);
            TestSupport.ApplyProviderLimits(options);
            ProviderOptions.ApplyParallelism(options, parallelism);
            var node = new MajDHTNode(options, contentStore: store, contentFallback: replica);
            replica.Attach(node);
            await TestSupport.StartAsync(node, cancellationToken);
            return new ReplicaHarness(charts.RootPath, node, replica);
        }

        /// <summary>Stops the replica node and drains the replica's pulls.</summary>
        /// <returns>Completion after both stopped.</returns>
        public async ValueTask DisposeAsync()
        {
            await Node.DisposeAsync();
            await Replica.DisposeAsync();
        }
    }
}
