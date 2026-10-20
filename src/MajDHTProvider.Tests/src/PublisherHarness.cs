using System;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProvider.Charts;
using MajDHTProvider.Hosting;
using MajDHTProvider.Publishing;
using MajDHTProvider.Storage;
using Microsoft.Extensions.Logging.Abstractions;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>A started publisher node and its publisher-mode components.</summary>
    internal sealed class PublisherHarness : IAsyncDisposable
    {
        /// <summary>Creates a harness.</summary>
        /// <param name="node">The started publisher node.</param>
        /// <param name="publisher">The publisher.</param>
        /// <param name="store">The block store.</param>
        /// <param name="ledgerPath">The ledger file path.</param>
        private PublisherHarness(MajDHTNode node, ChartPublisher publisher, FileBlockStore store, string ledgerPath)
        {
            Node = node;
            Publisher = publisher;
            Store = store;
            LedgerPath = ledgerPath;
        }

        /// <summary>Gets the started publisher node.</summary>
        public MajDHTNode Node { get; }

        /// <summary>Gets the publisher.</summary>
        public ChartPublisher Publisher { get; }

        /// <summary>Gets the block store.</summary>
        public FileBlockStore Store { get; }

        /// <summary>Gets the ledger file path.</summary>
        public string LedgerPath { get; }

        /// <summary>Starts a publisher for <c>&lt;directory&gt;/root</c> with its ledger in <c>&lt;directory&gt;/data</c>.</summary>
        /// <param name="directory">The test directory.</param>
        /// <param name="identity">The publisher identity.</param>
        /// <param name="recordLifetime">The publication lifetime.</param>
        /// <param name="cancellationToken">The test deadline.</param>
        /// <param name="parallelism">How many resources the publisher and its node handle at once.</param>
        /// <returns>The started harness.</returns>
        public static async Task<PublisherHarness> StartAsync(TestDirectory directory, DHTIdentity identity, TimeSpan recordLifetime,
            CancellationToken cancellationToken, int parallelism = 4)
        {
            var store = new FileBlockStore(1024 * 1024);
            var options = TestSupport.NodeOptions(DHTCapabilities.Download | DHTCapabilities.Provide);
            options.RecordLifetime = recordLifetime;
            TestSupport.ApplyProviderLimits(options);
            ProviderOptions.ApplyParallelism(options, parallelism);
            var node = new MajDHTNode(options, identity, store);
            await node.StartAsync(cancellationToken);
            var ledgerPath = directory.Combine("data", PublicationLedger.FileName);
            var publisher = new ChartPublisher(node, new ChartDirectory(directory.Combine("root")), store,
                PublicationLedger.Load(ledgerPath), options, parallelism, NullLogger.Instance);
            return new PublisherHarness(node, publisher, store, ledgerPath);
        }

        /// <summary>Stops the publisher node.</summary>
        /// <returns>Completion after the node stopped.</returns>
        public ValueTask DisposeAsync()
        {
            return Node.DisposeAsync();
        }
    }
}
