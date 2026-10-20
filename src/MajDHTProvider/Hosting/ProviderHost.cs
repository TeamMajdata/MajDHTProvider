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
    /// Runs one provider process: loads the identity, starts the DHT node with the file-backed block store, joins the
    /// network through the saved routing table and the bootstrap endpoints, synchronizes the chart directory once and then
    /// keeps it synchronized through directory notifications and periodic maintenance. The routing table is saved after
    /// joining and at shutdown, so the next start rejoins through the nodes known now.
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
        /// <exception cref="IOException">
        /// The chart root, data directory or a listener port is unavailable, or no bootstrap endpoint answered and no saved
        /// routing contact could be restored.
        /// </exception>
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
            var routingPath = Path.Combine(dataDirectory, RoutingTableFile.FileName);
            var replica = _options.Mode == ProviderMode.Replica
                ? new ChartReplica(charts, store, _options.PublisherId!, Path.Combine(dataDirectory, StagingDirectoryName),
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
                            nodeOptions, _options.Parallelism, _loggerFactory.CreateLogger<ChartPublisher>());
                    await RunSynchronizationAsync(charts, synchronizer, cancellationToken).ConfigureAwait(false);
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
