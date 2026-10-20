using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProvider.Charts;
using MajDHTProvider.Hosting;
using MajDHTProvider.Storage;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProvider.Replication
{
    /// <summary>
    /// Replica mode: serves a trusted publisher's chart resources from the local directory. When a peer requests a block of
    /// a resource that is not served yet, the replica checks its local file first and otherwise pulls the file from the
    /// origin through the DHT, stores it and serves it. Local files are deleted only after the publisher withdraws their
    /// key; a file removed or changed by hand is merely no longer served, and an outdated file is replaced by the current
    /// publication. Within one synchronization or refresh, the resources are resolved, verified and imported several at a
    /// time.
    /// </summary>
    internal sealed class ChartReplica : IChartSynchronizer, IDHTContentFallback, IAsyncDisposable
    {
        /// <summary>The chart layout being served.</summary>
        private readonly ChartDirectory _charts;
        /// <summary>The block store that serves local files.</summary>
        private readonly FileBlockStore _store;
        /// <summary>The trusted publisher fingerprint.</summary>
        private readonly string _publisherId;
        /// <summary>The directory receiving downloads before they are moved into the chart directory.</summary>
        private readonly string _stagingDirectory;
        /// <summary>How long a peer request waits for a pull.</summary>
        private readonly TimeSpan _pullWait;
        /// <summary>The interval between withdrawal and update checks.</summary>
        private readonly TimeSpan _refreshInterval;
        /// <summary>How many resources are resolved, verified and imported at once.</summary>
        private readonly int _parallelism;
        /// <summary>Receives replica diagnostics.</summary>
        private readonly ILogger _logger;
        /// <summary>Serializes every change of the served resources and the local files.</summary>
        private readonly SemaphoreSlim _sync = new(1, 1);
        /// <summary>The resources currently served, keyed by key name; every access holds <see cref="_gate"/>.</summary>
        private readonly Dictionary<string, ServedResource> _served = new(StringComparer.Ordinal);
        /// <summary>
        /// Protects <see cref="_pulls"/> and <see cref="_served"/>, which are read by peer requests that do not hold
        /// <see cref="_sync"/> and updated by the resources of one synchronization at once.
        /// </summary>
        private readonly Lock _gate = new();
        /// <summary>The pulls in progress, keyed by key name, so concurrent requests share one pull.</summary>
        private readonly Dictionary<string, Task<bool>> _pulls = new(StringComparer.Ordinal);
        /// <summary>Cancels pulls when the replica shuts down.</summary>
        private readonly CancellationTokenSource _lifetime = new();
        /// <summary>The node serving the resources, attached after it was created with this fallback.</summary>
        private MajDHTNode? _node;

        /// <summary>Creates a replica that is idle until a node is attached.</summary>
        /// <param name="charts">The chart layout being served.</param>
        /// <param name="store">The block store the node is created with.</param>
        /// <param name="publisherId">The trusted publisher fingerprint.</param>
        /// <param name="stagingDirectory">The directory for downloads in progress, ideally on the chart root's volume.</param>
        /// <param name="pullWait">How long a peer request waits for a pull before the peer fails over.</param>
        /// <param name="refreshInterval">The interval between withdrawal and update checks.</param>
        /// <param name="parallelism">How many resources are resolved, verified and imported at once, at least one.</param>
        /// <param name="logger">Receives replica diagnostics.</param>
        /// <exception cref="ArgumentOutOfRangeException">The parallelism is not positive.</exception>
        public ChartReplica(ChartDirectory charts, FileBlockStore store, string publisherId, string stagingDirectory,
            TimeSpan pullWait, TimeSpan refreshInterval, int parallelism, ILogger logger)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
            _charts = charts;
            _store = store;
            _publisherId = publisherId;
            _stagingDirectory = Path.GetFullPath(stagingDirectory);
            _pullWait = pullWait;
            _refreshInterval = refreshInterval;
            _parallelism = parallelism;
            _logger = logger;
        }

        /// <summary>Gets the number of resources currently served.</summary>
        internal int ServedCount
        {
            get
            {
                lock (_gate)
                {
                    return _served.Count;
                }
            }
        }

        /// <summary>Gets the node, which must be attached before the replica is used.</summary>
        /// <exception cref="InvalidOperationException">No node is attached.</exception>
        private MajDHTNode Node => _node ?? throw new InvalidOperationException("Attach the node before using the replica.");

        /// <summary>Attaches the node that was created with this replica as its content fallback.</summary>
        /// <param name="node">The node.</param>
        /// <exception cref="InvalidOperationException">A node is already attached.</exception>
        public void Attach(MajDHTNode node)
        {
            if (Interlocked.CompareExchange(ref _node, node, null) != null)
            {
                throw new InvalidOperationException("A node is already attached to the replica.");
            }
        }

        /// <summary>
        /// Handles a peer's block request for a resource that is not served yet: serves the local file when it matches the
        /// publication, and otherwise pulls the file from the origin. Requests for other publishers or other key names are
        /// declined.
        /// </summary>
        /// <param name="record">The signed manifest the peer requested.</param>
        /// <param name="cancellationToken">The peer request lifetime; the pull continues after it ends.</param>
        /// <returns>Whether the resource is served now.</returns>
        /// <exception cref="OperationCanceledException">The peer request ended.</exception>
        public async ValueTask<bool> TryAcquireAsync(DHTRecord record, CancellationToken cancellationToken)
        {
            if (!TryGetTrustedResource(record.Key, out var resource))
            {
                _logger.LogDebug("Declined a block request for a key outside the trusted chart namespace.");
                return false;
            }
            var pull = GetOrStartPull(resource, record);
            try
            {
                return await pull.WaitAsync(_pullWait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("{Key} is still being pulled; the requesting peer fails over meanwhile.", resource.KeyName);
                return false;
            }
        }

        /// <summary>Serves every local resource that matches its publication and stops serving resources removed by hand.</summary>
        /// <param name="cancellationToken">Stops synchronization.</param>
        /// <returns>Completion after every resource was synchronized.</returns>
        /// <exception cref="IOException">The chart root cannot be enumerated.</exception>
        /// <exception cref="OperationCanceledException">Synchronization was cancelled.</exception>
        public async Task SynchronizeAllAsync(CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var files = _charts.Scan();
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in files)
                {
                    present.Add(file.Resource.KeyName);
                }
                foreach (var served in SnapshotServed())
                {
                    if (!present.Contains(served.Resource.KeyName))
                    {
                        StopServingLocked(served, "its file was removed locally");
                    }
                }
                await AdoptLockedAsync(files, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Serving {Count} chart resources from {Root}.", ServedCount, _charts.RootPath);
            }
            finally
            {
                _sync.Release();
            }
        }

        /// <summary>Serves the new or changed resources of one chart and stops serving its resources removed by hand.</summary>
        /// <param name="chartId">The chart identifier.</param>
        /// <param name="cancellationToken">Stops synchronization.</param>
        /// <returns>Completion after the chart was synchronized.</returns>
        /// <exception cref="IOException">The chart directory cannot be enumerated.</exception>
        /// <exception cref="OperationCanceledException">Synchronization was cancelled.</exception>
        public async Task SynchronizeChartAsync(Guid chartId, CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var files = _charts.ScanChart(chartId);
                var present = new HashSet<ChartResourceKind>();
                foreach (var file in files)
                {
                    present.Add(file.Resource.Kind);
                }
                foreach (var served in SnapshotServed())
                {
                    if (served.Resource.ChartId == chartId && !present.Contains(served.Resource.Kind))
                    {
                        StopServingLocked(served, "its file was removed locally");
                    }
                }
                await AdoptLockedAsync(files, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sync.Release();
            }
        }

        /// <summary>Checks served resources for withdrawals and updates at every refresh interval until cancellation.</summary>
        /// <param name="cancellationToken">Stops refreshing.</param>
        /// <returns>A task that completes only by cancellation.</returns>
        /// <exception cref="OperationCanceledException">Refreshing was cancelled.</exception>
        public async Task RunMaintenanceAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await Task.Delay(_refreshInterval, cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Resolves every served resource, several at a time: deletes withdrawn resources, follows renewals and pulls updated
        /// content, then retries local files that could not be served before.
        /// </summary>
        /// <param name="cancellationToken">Stops refreshing.</param>
        /// <returns>Completion after every resource was checked.</returns>
        /// <exception cref="OperationCanceledException">Refreshing was cancelled.</exception>
        internal async Task RefreshAsync(CancellationToken cancellationToken)
        {
            List<ServedResource> served;
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                served = SnapshotServed();
            }
            finally
            {
                _sync.Release();
            }
            await ChartSynchronization.ForEachAsync(served, _parallelism, RefreshResourceAsync, cancellationToken).ConfigureAwait(false);
            try
            {
                await SynchronizeAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Rescanning {Root} during refresh failed.", _charts.RootPath);
            }
        }

        /// <summary>Waits until every pull in progress has finished.</summary>
        /// <returns>Completion after the pulls finished.</returns>
        internal Task WhenPullsCompleteAsync()
        {
            lock (_gate)
            {
                return Task.WhenAll(_pulls.Values);
            }
        }

        /// <summary>Cancels and drains every pull in progress.</summary>
        /// <returns>Completion after the pulls finished.</returns>
        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await WhenPullsCompleteAsync().ConfigureAwait(false);
            _lifetime.Dispose();
        }

        /// <summary>Resolves the latest publication of one served resource and applies it.</summary>
        /// <param name="resource">The served resource.</param>
        /// <param name="cancellationToken">Stops the refresh.</param>
        /// <returns>Completion after the resource was checked; a failed lookup is logged and checked again at the next refresh.</returns>
        /// <exception cref="OperationCanceledException">The refresh was cancelled.</exception>
        private async Task RefreshResourceAsync(ServedResource resource, CancellationToken cancellationToken)
        {
            DHTRecord? current;
            try
            {
                current = await Node.ResolveAsync(CreateKey(resource.Resource), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Resolving {Key} failed; it is checked again at the next refresh.", resource.Resource.KeyName);
                return;
            }
            if (current != null)
            {
                await ApplyResolvedRecordAsync(resource, current, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Applies the latest publication of a served resource found during a refresh.</summary>
        /// <param name="resource">The served resource as it was before the lookup.</param>
        /// <param name="current">The latest record of its key.</param>
        /// <param name="cancellationToken">Stops the update.</param>
        /// <returns>Completion after the record was applied.</returns>
        /// <exception cref="OperationCanceledException">The update was cancelled.</exception>
        private async Task ApplyResolvedRecordAsync(ServedResource resource, DHTRecord current, CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var keyName = resource.Resource.KeyName;
                if (!TryGetServed(keyName, out var latest) || latest != resource || current.Revision <= resource.Record.Revision)
                {
                    return;
                }
                if (current.IsWithdrawn)
                {
                    DeleteWithdrawnLocked(resource.Resource, resource.Path);
                    return;
                }
                if (current.FileHash != resource.Record.FileHash)
                {
                    _logger.LogInformation("{Key} was updated by the publisher; pulling revision {Revision}.", keyName, current.Revision);
                    _ = GetOrStartPull(resource.Resource, current);
                    return;
                }
                try
                {
                    await Node.AnnounceAsync(current, cancellationToken).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    // The earlier record expired and its content was dropped; register the unchanged file again.
                    await using var source = OpenRead(resource.Path);
                    await Node.ImportContentAsync(current, source, cancellationToken).ConfigureAwait(false);
                }
                SetServed(resource with { Record = current });
                _logger.LogDebug("Following renewal of {Key} to revision {Revision}.", keyName, current.Revision);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Applying the latest publication of {Key} failed.", resource.Resource.KeyName);
            }
            finally
            {
                _sync.Release();
            }
        }

        /// <summary>Starts a pull of a resource, or joins the pull already in progress.</summary>
        /// <param name="resource">The resource.</param>
        /// <param name="record">The publication to pull.</param>
        /// <returns>The pull, which completes with whether the resource is served.</returns>
        private Task<bool> GetOrStartPull(ChartResource resource, DHTRecord record)
        {
            lock (_gate)
            {
                if (_pulls.TryGetValue(resource.KeyName, out var existing))
                {
                    return existing;
                }
                var pull = Task.Run(() => PullAsync(resource, record, _lifetime.Token));
                _pulls.Add(resource.KeyName, pull);
                return pull;
            }
        }

        /// <summary>Serves a resource from its matching local file, or downloads it from the origin and stores it first.</summary>
        /// <param name="resource">The resource.</param>
        /// <param name="record">The publication to serve.</param>
        /// <param name="cancellationToken">The replica lifetime.</param>
        /// <returns>Whether the resource is served.</returns>
        private async Task<bool> PullAsync(ChartResource resource, DHTRecord record, CancellationToken cancellationToken)
        {
            try
            {
                if (await TryServeLocalCopyAsync(resource, record, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
                await DownloadFromOriginAsync(resource, record, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Pulling {Key} from the origin failed.", resource.KeyName);
                return false;
            }
            finally
            {
                lock (_gate)
                {
                    _pulls.Remove(resource.KeyName);
                }
            }
        }

        /// <summary>Serves the local file of a resource when its content matches the publication.</summary>
        /// <param name="resource">The resource.</param>
        /// <param name="record">The publication.</param>
        /// <param name="cancellationToken">Stops the import.</param>
        /// <returns>Whether the local file is served.</returns>
        /// <exception cref="IOException">The file or the content store failed.</exception>
        /// <exception cref="InvalidDataException">The node rejected the publication.</exception>
        /// <exception cref="OperationCanceledException">The import was cancelled.</exception>
        private async Task<bool> TryServeLocalCopyAsync(ChartResource resource, DHTRecord record, CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var file = _charts.FindFile(resource);
                if (file == null || !await TryImportLocalFileAsync(file.Value.Path, record, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
                SetServed(new ServedResource(resource, record, file.Value.Path, file.Value.Stamp));
                _logger.LogInformation("Serving {Key} from the local file {Path}.", resource.KeyName, file.Value.Path);
                return true;
            }
            finally
            {
                _sync.Release();
            }
        }

        /// <summary>
        /// Serves a local file under a publication when its content matches it, reading the file once to both verify it
        /// with the node and index the blocks the store serves from it.
        /// </summary>
        /// <param name="path">The local file.</param>
        /// <param name="record">The publication.</param>
        /// <param name="cancellationToken">Stops the import.</param>
        /// <returns>Whether the content matched and is now registered with the node.</returns>
        /// <exception cref="IOException">The file or the content store failed.</exception>
        /// <exception cref="InvalidDataException">The node rejected the publication itself.</exception>
        /// <exception cref="OperationCanceledException">The import was cancelled.</exception>
        private async Task<bool> TryImportLocalFileAsync(string path, DHTRecord record, CancellationToken cancellationToken)
        {
            await using var source = _store.OpenIndexing(path, record.BlockSize);
            try
            {
                await Node.ImportContentAsync(record, source, cancellationToken).ConfigureAwait(false);
                await source.CompleteAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (InvalidDataException)
            {
                // The node rejects differing content only after reading it all; a matching digest means it rejected the record itself.
                var digest = await source.CompleteAsync(cancellationToken).ConfigureAwait(false);
                if (digest.FileHash == record.FileHash && digest.Length == record.Length)
                {
                    throw;
                }
                _store.Remove(path);
                return false;
            }
        }

        /// <summary>Downloads a resource from the DHT into a staging file, then moves it into the chart directory and serves it.</summary>
        /// <param name="resource">The resource.</param>
        /// <param name="record">The publication.</param>
        /// <param name="cancellationToken">Stops the download.</param>
        /// <returns>Completion after the file was stored and served.</returns>
        /// <exception cref="FileNotFoundException">The publisher withdrew the key.</exception>
        /// <exception cref="InvalidDataException">The downloaded content failed verification.</exception>
        /// <exception cref="IOException">No provider was reachable or the file could not be stored.</exception>
        /// <exception cref="OperationCanceledException">The download was cancelled.</exception>
        private async Task DownloadFromOriginAsync(ChartResource resource, DHTRecord record, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_stagingDirectory);
            var staging = Path.Combine(_stagingDirectory, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                _logger.LogInformation("Pulling {Key} revision {Revision} ({Length} bytes) from the origin.", resource.KeyName, record.Revision, record.Length);
                await using (var destination = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await Node.DownloadAsync(record, destination, record.FileHash, cancellationToken).ConfigureAwait(false);
                }
                await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var target = _charts.GetTargetPath(resource);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(staging, target, true);
                    var stamp = FileStamp.Read(target);
                    await _store.IndexFileAsync(target, record.BlockSize, cancellationToken).ConfigureAwait(false);
                    SetServed(new ServedResource(resource, record, target, stamp));
                    _logger.LogInformation("Stored {Key} at {Path} and serving it.", resource.KeyName, target);
                }
                finally
                {
                    _sync.Release();
                }
            }
            finally
            {
                File.Delete(staging);
            }
        }

        /// <summary>
        /// Serves local resource files after checking them against their current publications. Files that are served
        /// unchanged are skipped; files that changed first stop being served, one after the other, and only then are the
        /// remaining files resolved, verified and imported several at a time, so no content stops being provided while
        /// another file with the same content is being imported.
        /// </summary>
        /// <param name="files">The resource files found on disk.</param>
        /// <param name="cancellationToken">Stops the checks.</param>
        /// <returns>Completion after every file was served, deleted, scheduled for replacement or left unserved.</returns>
        /// <exception cref="OperationCanceledException">The checks were cancelled.</exception>
        /// <remarks>The caller must hold <see cref="_sync"/>. Failures are logged; the next change, rescan or refresh retries.</remarks>
        private async Task AdoptLockedAsync(IReadOnlyList<ChartFile> files, CancellationToken cancellationToken)
        {
            var pending = new List<ChartFile>();
            foreach (var file in files)
            {
                if (TryGetServed(file.Resource.KeyName, out var existing) && existing.Path == file.Path && existing.Stamp == file.Stamp)
                {
                    continue;
                }
                if (existing != null)
                {
                    StopServingLocked(existing, "its file changed");
                }
                pending.Add(file);
            }
            await ChartSynchronization.ForEachAsync(pending, _parallelism, AdoptFileLockedAsync, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Serves one local resource file that is not served yet after checking it against the current publication.</summary>
        /// <param name="file">The resource file, with the stamp it had when it was found.</param>
        /// <param name="cancellationToken">Stops the check.</param>
        /// <returns>Completion after the file was served, deleted, scheduled for replacement or left unserved.</returns>
        /// <exception cref="OperationCanceledException">The check was cancelled.</exception>
        /// <remarks>
        /// The caller must hold <see cref="_sync"/> and may run this for several different keys at once. Failures are logged;
        /// the next change, rescan or refresh retries.
        /// </remarks>
        private async Task AdoptFileLockedAsync(ChartFile file, CancellationToken cancellationToken)
        {
            var keyName = file.Resource.KeyName;
            try
            {
                var record = await Node.ResolveAsync(CreateKey(file.Resource), cancellationToken).ConfigureAwait(false);
                if (record == null)
                {
                    _logger.LogInformation("No publication of {Key} is reachable; {Path} stays unserved until the next refresh.", keyName, file.Path);
                    return;
                }
                if (record.IsWithdrawn)
                {
                    DeleteWithdrawnLocked(file.Resource, file.Path);
                    return;
                }
                if (!await TryImportLocalFileAsync(file.Path, record, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("{Path} differs from revision {Revision} of {Key}; pulling the current version.", file.Path, record.Revision, keyName);
                    _ = GetOrStartPull(file.Resource, record);
                    return;
                }
                SetServed(new ServedResource(file.Resource, record, file.Path, file.Stamp));
                _logger.LogInformation("Serving {Key} from the local file {Path}.", keyName, file.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not read {Path} to serve {Key}; it is retried on the next change or rescan.", file.Path, keyName);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Serving {Key} from {Path} failed.", keyName, file.Path);
            }
        }

        /// <summary>Deletes the local file of a withdrawn resource, and its chart directory when nothing else remains in it.</summary>
        /// <param name="resource">The withdrawn resource.</param>
        /// <param name="path">The local file.</param>
        /// <remarks>The caller must hold <see cref="_sync"/>.</remarks>
        private void DeleteWithdrawnLocked(ChartResource resource, string path)
        {
            if (TryGetServed(resource.KeyName, out var served))
            {
                StopServingLocked(served, "the publisher withdrew it");
            }
            _store.Remove(path);
            try
            {
                File.Delete(path);
                var directory = Path.GetDirectoryName(path);
                if (directory != null && Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory);
                }
                _logger.LogInformation("Deleted {Path} because the publisher withdrew {Key}.", path, resource.KeyName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not delete {Path} of the withdrawn {Key}; it is retried at the next refresh.", path, resource.KeyName);
            }
        }

        /// <summary>Stops serving a resource, keeping its file.</summary>
        /// <param name="served">The served resource.</param>
        /// <param name="reason">Explains why the resource is no longer served.</param>
        /// <remarks>The caller must hold <see cref="_sync"/>.</remarks>
        private void StopServingLocked(ServedResource served, string reason)
        {
            lock (_gate)
            {
                _served.Remove(served.Resource.KeyName);
            }
            _store.Remove(served.Path);
            var shared = false;
            foreach (var other in SnapshotServed())
            {
                shared |= other.Record.FileHash == served.Record.FileHash;
            }
            if (!shared)
            {
                Node.StopProviding(served.Record.FileHash);
            }
            _logger.LogInformation("Stopped serving {Key} because {Reason}.", served.Resource.KeyName, reason);
        }

        /// <summary>Records a served resource.</summary>
        /// <param name="served">The served resource.</param>
        private void SetServed(ServedResource served)
        {
            lock (_gate)
            {
                _served[served.Resource.KeyName] = served;
            }
        }

        /// <summary>Gets the served resource of a key.</summary>
        /// <param name="keyName">The key name.</param>
        /// <param name="served">Receives the served resource when the key is served.</param>
        /// <returns>Whether the key is served.</returns>
        private bool TryGetServed(string keyName, [MaybeNullWhen(false)] out ServedResource served)
        {
            lock (_gate)
            {
                return _served.TryGetValue(keyName, out served);
            }
        }

        /// <summary>Copies the served resources.</summary>
        /// <returns>A snapshot of the served resources.</returns>
        private List<ServedResource> SnapshotServed()
        {
            lock (_gate)
            {
                return [.. _served.Values];
            }
        }

        /// <summary>Checks that a requested key is a chart resource key of the trusted publisher.</summary>
        /// <param name="key">The requested key.</param>
        /// <param name="resource">Receives the resource when the key is trusted.</param>
        /// <returns>Whether the replica serves the key.</returns>
        private bool TryGetTrustedResource(DHTKey key, out ChartResource resource)
        {
            resource = default;
            return key.IsValid && key.PublisherId == _publisherId && ChartResource.TryParseKeyName(key.Name, out resource);
        }

        /// <summary>Creates the trusted publisher's key of a resource.</summary>
        /// <param name="resource">The resource.</param>
        /// <returns>The publisher-scoped key.</returns>
        private DHTKey CreateKey(ChartResource resource)
        {
            return new DHTKey(_publisherId, resource.KeyName);
        }

        /// <summary>Opens a local file for reading while other processes may still access it.</summary>
        /// <param name="path">The file path.</param>
        /// <returns>The open stream.</returns>
        /// <exception cref="IOException">The file cannot be opened.</exception>
        private static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        /// <summary>A resource the replica serves from a local file.</summary>
        /// <param name="Resource">The served resource.</param>
        /// <param name="Record">The latest known publication.</param>
        /// <param name="Path">The local file.</param>
        /// <param name="Stamp">The file version that was verified.</param>
        private sealed record ServedResource(ChartResource Resource, DHTRecord Record, string Path, FileStamp Stamp);
    }
}
