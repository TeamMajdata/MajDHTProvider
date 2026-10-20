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
    /// Runs one provider process: loads the identity, starts the DHT node, joins the network through the saved routing
    /// table and the bootstrap endpoints, and then either keeps the chart directory synchronized through directory
    /// notifications and periodic maintenance, or, in bootstrap mode, only keeps introducing peers to each other. The
    /// routing table is saved after joining, periodically and at shutdown, so the next start rejoins through the nodes
    /// known now.
    /// </summary>
    internal sealed class ProviderHost
    {
        /// <summary>The name of the staging directory for replica downloads inside the data directory.</summary>
        public const string StagingDirectoryName = "staging";

        /// <summary>The interval between periodic saves of the routing table while the provider runs.</summary>
        private static readonly TimeSpan s_routingSnapshotInterval = TimeSpan.FromMinutes(10);

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
        /// <exception cref="IOException">
        /// The chart root, data directory or a listener port is unavailable, or no bootstrap endpoint answered and no saved
        /// routing contact could be restored.
        /// </exception>
        /// <exception cref="System.Net.Sockets.SocketException">A listener port could not be bound.</exception>
        /// <exception cref="OperationCanceledException">The provider was stopped.</exception>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var dataDirectory = _options.GetDataDirectory();
            Directory.CreateDirectory(dataDirectory);
            var identity = IdentityFile.LoadOrCreate(Path.Combine(dataDirectory, IdentityFile.FileName), _options.IdentityWorkBits,
                _logger, cancellationToken);
            var nodeOptions = _options.CreateNodeOptions(_loggerFactory.CreateLogger(typeof(MajDHTNode).FullName!));
            var routingPath = Path.Combine(dataDirectory, RoutingTableFile.FileName);
            if (_options.Mode == ProviderMode.Bootstrap)
            {
                await RunBootstrapAsync(nodeOptions, identity, routingPath, cancellationToken).ConfigureAwait(false);
                return;
            }
            var charts = new ChartDirectory(_options.RootDirectory);
            var store = new FileBlockStore(_options.BlockCacheCapacity);
            var leaves = new LeafCache(Path.Combine(dataDirectory, LeafCache.DirectoryName), _loggerFactory.CreateLogger<LeafCache>());
            var replica = _options.Mode == ProviderMode.Replica
                ? new ChartReplica(charts, store, leaves, _options.PublisherId!, Path.Combine(dataDirectory, StagingDirectoryName),
                    _options.PullWait, _options.RefreshInterval, _options.Parallelism, _loggerFactory.CreateLogger<ChartReplica>())
                : null;
            try
            {
                await using var node = new MajDHTNode(nodeOptions, identity, store, contentFallback: replica);
                replica?.Attach(node);
                await node.StartAsync(cancellationToken).ConfigureAwait(false);
                LogStarted(node);
                try
                {
                    await JoinNetworkAsync(node, nodeOptions.BootstrapEndpoints.Count, routingPath, cancellationToken).ConfigureAwait(false);
                    IChartSynchronizer synchronizer = (IChartSynchronizer?)replica
                        ?? new ChartPublisher(node, charts, store, PublicationLedger.Load(Path.Combine(dataDirectory, PublicationLedger.FileName)),
                            leaves, nodeOptions, _options.Parallelism, _loggerFactory.CreateLogger<ChartPublisher>());
                    await RunSynchronizationAsync(node, routingPath, charts, synchronizer, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    SaveRoutingTable(node, routingPath);
                }
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
        /// Runs a bootstrap node: joins the network, then only keeps the routing table alive and saved, so peers that
        /// contact this node learn about each other while it publishes and serves nothing.
        /// </summary>
        /// <param name="nodeOptions">The node configuration without content capabilities.</param>
        /// <param name="identity">The persistent identity.</param>
        /// <param name="routingPath">The routing table snapshot file.</param>
        /// <param name="cancellationToken">Stops the node.</param>
        /// <returns>A task that completes only by cancellation or failure.</returns>
        /// <exception cref="IOException">No bootstrap endpoint answered and no saved routing contact could be restored.</exception>
        /// <exception cref="OperationCanceledException">The node was stopped.</exception>
        private async Task RunBootstrapAsync(DHTNodeOptions nodeOptions, DHTIdentity identity, string routingPath, CancellationToken cancellationToken)
        {
            await using var node = new MajDHTNode(nodeOptions, identity);
            await node.StartAsync(cancellationToken).ConfigureAwait(false);
            LogStarted(node);
            try
            {
                await JoinNetworkAsync(node, nodeOptions.BootstrapEndpoints.Count, routingPath, cancellationToken).ConfigureAwait(false);
                await RunRoutingSnapshotsAsync(node, routingPath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SaveRoutingTable(node, routingPath);
            }
        }

        /// <summary>Saves the routing table at every snapshot interval until cancellation, so a crash loses few contacts.</summary>
        /// <param name="node">The started node.</param>
        /// <param name="routingPath">The routing table snapshot file.</param>
        /// <param name="cancellationToken">Stops saving.</param>
        /// <returns>A task that completes only by cancellation.</returns>
        /// <exception cref="OperationCanceledException">Saving was stopped.</exception>
        private async Task RunRoutingSnapshotsAsync(MajDHTNode node, string routingPath, CancellationToken cancellationToken)
        {
            while (true)
            {
                await Task.Delay(s_routingSnapshotInterval, cancellationToken).ConfigureAwait(false);
                SaveRoutingTable(node, routingPath);
            }
        }

        /// <summary>
        /// Joins the network through the saved routing table and the configured bootstrap endpoints at the same time, then
        /// saves the routing table so the contacts learned now survive a crash.
        /// </summary>
        /// <param name="node">The started node.</param>
        /// <param name="bootstrapCount">The number of configured bootstrap endpoints.</param>
        /// <param name="routingPath">The routing table snapshot file.</param>
        /// <param name="cancellationToken">Stops joining.</param>
        /// <returns>Completion after both ways of joining ended.</returns>
        /// <exception cref="IOException">No bootstrap endpoint answered and no saved routing contact was restored.</exception>
        /// <exception cref="OperationCanceledException">Joining was stopped.</exception>
        private async Task JoinNetworkAsync(MajDHTNode node, int bootstrapCount, string routingPath, CancellationToken cancellationToken)
        {
            var snapshot = RoutingTableFile.TryLoad(routingPath, _logger);
            if (snapshot == null && bootstrapCount == 0)
            {
                _logger.LogInformation("No saved routing table and no bootstrap endpoints; waiting for peers to connect.");
                return;
            }
            var restoring = snapshot == null ? Task.FromResult(0) : RestoreRoutingTableAsync(node, snapshot, routingPath, cancellationToken);
            var bootstrapping = bootstrapCount == 0 ? Task.FromResult(0) : node.BootstrapAsync(cancellationToken);
            Task[] joining = [restoring, bootstrapping];
            await Task.WhenAll(joining).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            var restored = await restoring.ConfigureAwait(false);
            try
            {
                var answered = await bootstrapping.ConfigureAwait(false);
                if (bootstrapCount != 0)
                {
                    _logger.LogInformation("{Answered} of {Configured} bootstrap endpoints answered.", answered, bootstrapCount);
                }
                else if (restored == 0)
                {
                    _logger.LogWarning("No saved routing contact answered and no bootstrap endpoints are configured; waiting for peers to connect.");
                }
            }
            catch (IOException exception) when (restored != 0)
            {
                _logger.LogWarning(exception, "No bootstrap endpoint answered; continuing with {Restored} restored routing contacts.", restored);
            }
            SaveRoutingTable(node, routingPath);
        }

        /// <summary>Restores the saved routing table; an invalid snapshot is logged and ignored.</summary>
        /// <param name="node">The started node.</param>
        /// <param name="snapshot">The saved snapshot.</param>
        /// <param name="routingPath">The snapshot file, for diagnostics.</param>
        /// <param name="cancellationToken">Stops the restoration.</param>
        /// <returns>The number of saved contacts that answered under their saved identity.</returns>
        /// <exception cref="OperationCanceledException">The restoration was stopped.</exception>
        private async Task<int> RestoreRoutingTableAsync(MajDHTNode node, byte[] snapshot, string routingPath, CancellationToken cancellationToken)
        {
            try
            {
                var restored = await node.RestoreRoutingTableAsync(snapshot, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Restored {Restored} routing contacts from {Path}.", restored, routingPath);
                return restored;
            }
            catch (InvalidDataException exception)
            {
                _logger.LogWarning(exception, "The routing table {Path} is invalid and is ignored.", routingPath);
                return 0;
            }
        }

        /// <summary>
        /// Saves the routing table unless it is empty, so a snapshot taken while the network was reachable survives a start
        /// during which nothing answered.
        /// </summary>
        /// <param name="node">The node whose routing table is saved.</param>
        /// <param name="routingPath">The snapshot file.</param>
        private void SaveRoutingTable(MajDHTNode node, string routingPath)
        {
            var contacts = node.KnownPeers.Count;
            if (contacts == 0)
            {
                _logger.LogDebug("The routing table is empty and is not saved.");
                return;
            }
            if (RoutingTableFile.Save(routingPath, node.ExportRoutingTable(), _logger))
            {
                _logger.LogInformation("Saved the routing table with {Count} contacts to {Path}.", contacts, routingPath);
            }
        }

        /// <summary>
        /// Starts watching before the initial synchronization, so no change made meanwhile is lost, then runs watching,
        /// maintenance and periodic routing table saves until any of them stops.
        /// </summary>
        /// <param name="node">The started node.</param>
        /// <param name="routingPath">The routing table snapshot file.</param>
        /// <param name="charts">The chart layout.</param>
        /// <param name="synchronizer">The mode-specific synchronizer.</param>
        /// <param name="cancellationToken">Stops synchronization.</param>
        /// <returns>A task that completes only by cancellation or failure.</returns>
        /// <exception cref="IOException">The chart root cannot be enumerated.</exception>
        /// <exception cref="OperationCanceledException">Synchronization was stopped.</exception>
        private async Task RunSynchronizationAsync(MajDHTNode node, string routingPath, ChartDirectory charts, IChartSynchronizer synchronizer,
            CancellationToken cancellationToken)
        {
            using var watcher = new ChartDirectoryWatcher(charts, _options.QuietPeriod, _options.RescanInterval,
                _loggerFactory.CreateLogger<ChartDirectoryWatcher>());
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var watching = watcher.RunAsync(synchronizer.SynchronizeChartAsync, synchronizer.SynchronizeAllAsync, stopping.Token);
            var snapshots = RunRoutingSnapshotsAsync(node, routingPath, stopping.Token);
            var maintenance = Task.CompletedTask;
            try
            {
                await synchronizer.SynchronizeAllAsync(stopping.Token).ConfigureAwait(false);
                maintenance = synchronizer.RunMaintenanceAsync(stopping.Token);
                var stopped = await Task.WhenAny(watching, maintenance, snapshots).ConfigureAwait(false);
                await stopped.ConfigureAwait(false);
            }
            finally
            {
                await stopping.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(watching, maintenance, snapshots).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        /// <summary>Logs the node identity and listening endpoints; the identity seed is never logged.</summary>
        /// <param name="node">The started node.</param>
        private void LogStarted(MajDHTNode node)
        {
            _logger.LogInformation("{Mode} started with node ID {NodeId}.", _options.Mode, node.NodeId);
            switch (_options.Mode)
            {
                case ProviderMode.Publisher:
                    _logger.LogInformation("Publisher ID for replicas and clients: {PublisherId}", node.NodeId);
                    break;
                case ProviderMode.Replica:
                    _logger.LogInformation("Serving resources of publisher {PublisherId}.", _options.PublisherId);
                    break;
                default:
                    _logger.LogInformation("Introducing peers to each other{Relay}; no content is published or served.",
                        _options.EnableRelay ? " and assisting NAT traversal" : string.Empty);
                    break;
            }
            foreach (var endpoint in node.LocalPeer.Endpoints)
            {
                _logger.LogInformation("Listening on {Address} (TCP {TcpPort}, UDP {UdpPort}).", endpoint.Address, endpoint.TcpPort, endpoint.UdpPort);
            }
        }
    }
}
