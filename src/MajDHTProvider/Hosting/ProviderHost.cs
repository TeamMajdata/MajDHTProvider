using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProvider.Charts;
using MajDHTProvider.Publishing;
using MajDHTProvider.Replication;
using MajDHTProvider.Storage;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProvider.Hosting
{
    /// <summary>
    /// Runs one provider process: loads the identity, starts the DHT node with the file-backed block store, synchronizes
    /// the chart directory once and then keeps it synchronized through directory notifications and periodic maintenance.
    /// </summary>
    internal sealed class ProviderHost
    {
        /// <summary>The name of the staging directory for replica downloads inside the data directory.</summary>
        public const string StagingDirectoryName = "staging";

        /// <summary>The provider configuration.</summary>
        private readonly ProviderOptions _options;
        /// <summary>Creates the loggers of every component.</summary>
        private readonly ILoggerFactory _loggerFactory;
        /// <summary>Receives host diagnostics.</summary>
        private readonly ILogger _logger;

        /// <summary>Creates a host.</summary>
        /// <param name="options">The validated provider configuration.</param>
        /// <param name="loggerFactory">Creates the loggers of every component.</param>
        public ProviderHost(ProviderOptions options, ILoggerFactory loggerFactory)
        {
            _options = options;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<ProviderHost>();
        }

        /// <summary>Runs the provider until cancellation or an unrecoverable failure.</summary>
        /// <param name="cancellationToken">Stops the provider.</param>
        /// <returns>A task that completes only by cancellation or failure.</returns>
        /// <exception cref="ArgumentException">The node options are invalid.</exception>
        /// <exception cref="InvalidDataException">The identity file or publication ledger is invalid.</exception>
        /// <exception cref="IOException">The chart root, data directory or a listener port is unavailable.</exception>
        /// <exception cref="System.Net.Sockets.SocketException">A listener port could not be bound.</exception>
        /// <exception cref="OperationCanceledException">The provider was stopped.</exception>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var charts = new ChartDirectory(_options.RootDirectory);
            var dataDirectory = _options.GetDataDirectory();
            Directory.CreateDirectory(dataDirectory);
            var identity = IdentityFile.LoadOrCreate(Path.Combine(dataDirectory, IdentityFile.FileName), _options.IdentityWorkBits,
                _logger, cancellationToken);
            var store = new FileBlockStore(_options.BlockCacheCapacity);
            var nodeOptions = _options.CreateNodeOptions(_loggerFactory.CreateLogger(typeof(MajDHTNode).FullName!));
            var replica = _options.Mode == ProviderMode.Replica
                ? new ChartReplica(charts, store, _options.PublisherId!, Path.Combine(dataDirectory, StagingDirectoryName),
                    _options.PullWait, _options.RefreshInterval, _loggerFactory.CreateLogger<ChartReplica>())
                : null;
            try
            {
                await using var node = new MajDHTNode(nodeOptions, identity, store, contentFallback: replica);
                replica?.Attach(node);
                await node.StartAsync(cancellationToken).ConfigureAwait(false);
                LogStarted(node);
                if (nodeOptions.BootstrapEndpoints.Count != 0)
                {
                    var answered = await node.BootstrapAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("{Answered} of {Configured} bootstrap endpoints answered.", answered, nodeOptions.BootstrapEndpoints.Count);
                }
                IChartSynchronizer synchronizer = (IChartSynchronizer?)replica
                    ?? new ChartPublisher(node, charts, store, PublicationLedger.Load(Path.Combine(dataDirectory, PublicationLedger.FileName)),
                        nodeOptions, _loggerFactory.CreateLogger<ChartPublisher>());
                await RunSynchronizationAsync(charts, synchronizer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (replica != null)
                {
                    await replica.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Starts watching before the initial synchronization, so no change made meanwhile is lost, then runs watching and
        /// maintenance until either stops.
        /// </summary>
        /// <param name="charts">The chart layout.</param>
        /// <param name="synchronizer">The mode-specific synchronizer.</param>
        /// <param name="cancellationToken">Stops synchronization.</param>
        /// <returns>A task that completes only by cancellation or failure.</returns>
        /// <exception cref="IOException">The chart root cannot be enumerated.</exception>
        /// <exception cref="OperationCanceledException">Synchronization was stopped.</exception>
        private async Task RunSynchronizationAsync(ChartDirectory charts, IChartSynchronizer synchronizer, CancellationToken cancellationToken)
        {
            using var watcher = new ChartDirectoryWatcher(charts, _options.QuietPeriod, _options.RescanInterval,
                _loggerFactory.CreateLogger<ChartDirectoryWatcher>());
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var watching = watcher.RunAsync(synchronizer.SynchronizeChartAsync, synchronizer.SynchronizeAllAsync, stopping.Token);
            var maintenance = Task.CompletedTask;
            try
            {
                await synchronizer.SynchronizeAllAsync(stopping.Token).ConfigureAwait(false);
                maintenance = synchronizer.RunMaintenanceAsync(stopping.Token);
                var stopped = await Task.WhenAny(watching, maintenance).ConfigureAwait(false);
                await stopped.ConfigureAwait(false);
            }
            finally
            {
                await stopping.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(watching, maintenance).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        /// <summary>Logs the node identity and listening endpoints; the identity seed is never logged.</summary>
        /// <param name="node">The started node.</param>
        private void LogStarted(MajDHTNode node)
        {
            _logger.LogInformation("{Mode} started with node ID {NodeId}.", _options.Mode, node.NodeId);
            if (_options.Mode == ProviderMode.Publisher)
            {
                _logger.LogInformation("Publisher ID for replicas and clients: {PublisherId}", node.NodeId);
            }
            else
            {
                _logger.LogInformation("Serving resources of publisher {PublisherId}.", _options.PublisherId);
            }
            foreach (var endpoint in node.LocalPeer.Endpoints)
            {
                _logger.LogInformation("Listening on {Address} (TCP {TcpPort}, UDP {UdpPort}).", endpoint.Address, endpoint.TcpPort, endpoint.UdpPort);
            }
        }
    }
}
