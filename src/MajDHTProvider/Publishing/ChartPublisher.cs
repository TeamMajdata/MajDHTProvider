using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProvider.Charts;
using MajDHTProvider.Hosting;
using MajDHTProvider.Storage;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProvider.Publishing
{
    /// <summary>
    /// Publisher mode: publishes every chart resource file under the publisher's key namespace, republishes files whose
    /// content changed, withdraws keys whose files or chart directories were removed, and renews publications before
    /// their signed lifetime ends. Synchronizations and renewals are serialized, so a directory event and a rescan never
    /// race; within one of them the resources are read, published, withdrawn or renewed several at a time, and the ledger
    /// is saved once at the end instead of after every resource. The leaves and record of every published file are cached,
    /// so a restart registers unchanged files from the cache instead of reading them again.
    /// </summary>
    internal sealed class ChartPublisher : IChartSynchronizer
    {
        /// <summary>The node that signs and serves publications.</summary>
        private readonly MajDHTNode _node;
        /// <summary>The chart layout being published.</summary>
        private readonly ChartDirectory _charts;
        /// <summary>The block store that serves published files from disk.</summary>
        private readonly FileBlockStore _store;
        /// <summary>The persistent revisions of every signed key.</summary>
        private readonly PublicationLedger _ledger;
        /// <summary>The persistent leaves, records and stamps of published files.</summary>
        private readonly LeafCache _leaves;
        /// <summary>The block size the node signs publications with.</summary>
        private readonly int _blockSize;
        /// <summary>The signed lifetime of a publication.</summary>
        private readonly TimeSpan _recordLifetime;
        /// <summary>How many resources are published, withdrawn or renewed at once.</summary>
        private readonly int _parallelism;
        /// <summary>Receives publisher diagnostics.</summary>
        private readonly ILogger _logger;
        /// <summary>Serializes synchronizations and renewals.</summary>
        private readonly SemaphoreSlim _sync = new(1, 1);
        /// <summary>
        /// The publications this process signed and serves, keyed by key name. The resources of one synchronization update
        /// different keys at once, which is why the dictionary is concurrent.
        /// </summary>
        private readonly ConcurrentDictionary<string, Publication> _publications = new(StringComparer.Ordinal);

        /// <summary>Creates a publisher.</summary>
        /// <param name="node">The started node that signs and serves publications.</param>
        /// <param name="charts">The chart layout being published.</param>
        /// <param name="store">The block store the node was created with.</param>
        /// <param name="ledger">The persistent revisions of every signed key.</param>
        /// <param name="leaves">The persistent leaves, records and stamps of published files.</param>
        /// <param name="nodeOptions">The options the node was created with.</param>
        /// <param name="parallelism">How many resources are published, withdrawn or renewed at once, at least one.</param>
        /// <param name="logger">Receives publisher diagnostics.</param>
        /// <exception cref="ArgumentOutOfRangeException">The parallelism is not positive.</exception>
        public ChartPublisher(MajDHTNode node, ChartDirectory charts, FileBlockStore store, PublicationLedger ledger, LeafCache leaves,
            DHTNodeOptions nodeOptions, int parallelism, ILogger logger)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
            _node = node;
            _charts = charts;
            _store = store;
            _ledger = ledger;
            _leaves = leaves;
            _blockSize = nodeOptions.BlockSize;
            _recordLifetime = nodeOptions.RecordLifetime;
            _parallelism = parallelism;
            _logger = logger;
        }

        /// <summary>Publishes every resource on disk and withdraws every signed key whose resource is gone.</summary>
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
                await ChartSynchronization.ForEachAsync(files, _parallelism, PublishIfChangedLockedAsync, cancellationToken).ConfigureAwait(false);
                var removed = new List<string>();
                foreach (var keyName in GetSignedKeyNamesLocked())
                {
                    if (!present.Contains(keyName))
                    {
                        removed.Add(keyName);
                    }
                }
                await ChartSynchronization.ForEachAsync(removed, _parallelism, WithdrawLockedAsync, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Publishing {Count} chart resources from {Root}.", _publications.Count, _charts.RootPath);
            }
            finally
            {
                SaveLedgerLocked();
                _sync.Release();
            }
        }

        /// <summary>Publishes the changed resources of one chart and withdraws its removed resources.</summary>
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
                await ChartSynchronization.ForEachAsync(files, _parallelism, PublishIfChangedLockedAsync, cancellationToken).ConfigureAwait(false);
                var removed = new List<string>();
                foreach (var kind in ChartResource.AllKinds)
                {
                    var keyName = new ChartResource(chartId, kind).KeyName;
                    if (!present.Contains(kind) && IsPublishedLocked(keyName))
                    {
                        removed.Add(keyName);
                    }
                }
                await ChartSynchronization.ForEachAsync(removed, _parallelism, WithdrawLockedAsync, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SaveLedgerLocked();
                _sync.Release();
            }
        }

        /// <summary>Renews publications at half their lifetime until cancellation.</summary>
        /// <param name="cancellationToken">Stops renewing.</param>
        /// <returns>A task that completes only by cancellation.</returns>
        /// <exception cref="OperationCanceledException">Renewal was cancelled.</exception>
        public async Task RunMaintenanceAsync(CancellationToken cancellationToken)
        {
            var interval = TimeSpan.FromTicks(Math.Clamp(_recordLifetime.Ticks / 4, TimeSpan.TicksPerSecond, TimeSpan.TicksPerHour));
            while (true)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                await RenewDueAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Re-signs every publication with less than half of its lifetime left under a higher revision, without rereading
        /// unchanged files.
        /// </summary>
        /// <param name="cancellationToken">Stops renewing.</param>
        /// <returns>The number of renewed publications.</returns>
        /// <exception cref="OperationCanceledException">Renewal was cancelled.</exception>
        internal async Task<int> RenewDueAsync(CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var threshold = DateTimeOffset.UtcNow + (_recordLifetime / 2);
                var due = new List<Publication>();
                foreach (var publication in _publications.Values)
                {
                    if (publication.Record.ExpiresAt <= threshold)
                    {
                        due.Add(publication);
                    }
                }
                var renewed = 0;
                await ChartSynchronization.ForEachAsync(due, _parallelism, async (publication, token) =>
                {
                    if (await RenewLockedAsync(publication, token).ConfigureAwait(false))
                    {
                        Interlocked.Increment(ref renewed);
                    }
                }, cancellationToken).ConfigureAwait(false);
                if (due.Count != 0)
                {
                    _logger.LogInformation("Renewed {Renewed} of {Due} publications that reached half of their lifetime.", renewed, due.Count);
                }
                return renewed;
            }
            finally
            {
                SaveLedgerLocked();
                _sync.Release();
            }
        }

        /// <summary>Lists every key whose latest signed record, in the ledger or in this process, is a publication.</summary>
        /// <returns>The published key names.</returns>
        /// <remarks>The caller must hold <see cref="_sync"/>.</remarks>
        private HashSet<string> GetSignedKeyNamesLocked()
        {
            var signed = new HashSet<string>(_ledger.PublishedKeyNames, StringComparer.Ordinal);
            signed.UnionWith(_publications.Keys);
            return signed;
        }

        /// <summary>Checks whether the latest signed record of a key is a publication.</summary>
        /// <param name="keyName">The key name.</param>
        /// <returns>Whether the key is published.</returns>
        /// <remarks>The caller must hold <see cref="_sync"/>.</remarks>
        private bool IsPublishedLocked(string keyName)
        {
            return _publications.ContainsKey(keyName) || (_ledger.TryGet(keyName, out var entry) && !entry.Withdrawn);
        }

        /// <summary>Saves the ledger when it changed; a failure is logged because the next synchronization saves it again.</summary>
        /// <remarks>The caller must hold <see cref="_sync"/>.</remarks>
        private void SaveLedgerLocked()
        {
            try
            {
                _ledger.Save();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(exception, "Could not save the publication ledger; it is saved again after the next synchronization.");
            }
        }

        /// <summary>Publishes a resource file unless the same content is already published from it.</summary>
        /// <param name="file">The resource file.</param>
        /// <param name="cancellationToken">Stops publishing.</param>
        /// <returns>Completion after the resource was published or skipped.</returns>
        /// <exception cref="OperationCanceledException">Publishing was cancelled.</exception>
        /// <remarks>
        /// The caller must hold <see cref="_sync"/> and may run this for several different keys at once. Failures are logged;
        /// the next change or rescan retries.
        /// </remarks>
        private async Task PublishIfChangedLockedAsync(ChartFile file, CancellationToken cancellationToken)
        {
            var keyName = file.Resource.KeyName;
            try
            {
                _publications.TryGetValue(keyName, out var existing);
                if (existing != null && existing.Path == file.Path && existing.Stamp == file.Stamp)
                {
                    return;
                }
                if (existing == null && await TryResumeLockedAsync(file, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                if (existing != null)
                {
                    // The file changed on disk; hashing it first avoids a new revision when only its metadata changed.
                    var digest = await _store.IndexFileAsync(file.Path, _blockSize, cancellationToken).ConfigureAwait(false);
                    if (existing.Record.FileHash == digest.FileHash)
                    {
                        _publications[keyName] = existing with { Path = file.Path, Stamp = file.Stamp };
                        return;
                    }
                }
                var revision = _ledger.NextRevision(keyName);
                DHTRecord record;
                byte[] leaves;
                // One pass over the file both feeds the node and indexes the blocks the store serves from it.
                await using (var source = _store.OpenIndexing(file.Path, _blockSize))
                {
                    record = await _node.PublishAsync(keyName, source, revision, cancellationToken).ConfigureAwait(false);
                    await source.CompleteAsync(cancellationToken).ConfigureAwait(false);
                    leaves = source.ExportLeaves();
                }
                _ledger.RecordPublished(keyName, revision, record.FileHash);
                var unchangedDuringPublish = TryReadStamp(file.Path) == file.Stamp;
                if (unchangedDuringPublish)
                {
                    _leaves.Save(file.Resource, new CachedContent(file.Resource.Kind, file.Stamp, _blockSize, record.Length, record.FileHash, record.ToBytes(), leaves));
                }
                else
                {
                    _logger.LogWarning("{Key} changed while it was published; it is published again on the next change or rescan.", keyName);
                }
                // A default stamp never matches a real file, so content that changed during publishing is read again.
                _publications[keyName] = new Publication(file.Resource, record, file.Path, unchangedDuringPublish ? file.Stamp : default);
                _logger.LogInformation("Published {Key} revision {Revision} ({Length} bytes).", keyName, revision, record.Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Publishing {Key} from {Path} failed: {Reason} It is retried on the next change or rescan.",
                    keyName, file.Path, exception.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Publishing {Key} from {Path} failed.", keyName, file.Path);
            }
        }

        /// <summary>
        /// Registers a resource from the leaves, record and stamp cached when it was last published, without reading the
        /// file: the cached record is imported as it is while it has more than half of its lifetime left, and a fresh
        /// revision is signed from the leaves otherwise.
        /// </summary>
        /// <param name="file">The resource file, whose stamp must equal the cached stamp for the cache to be trusted.</param>
        /// <param name="cancellationToken">Stops publishing.</param>
        /// <returns>Whether the resource is published from its cache; false when there is no usable cache entry.</returns>
        /// <exception cref="IOException">The node's quotas refused the content.</exception>
        /// <exception cref="OperationCanceledException">Publishing was cancelled.</exception>
        /// <remarks>The caller must hold <see cref="_sync"/>, and this process must not have published the resource yet.</remarks>
        private async Task<bool> TryResumeLockedAsync(ChartFile file, CancellationToken cancellationToken)
        {
            var cached = _leaves.TryLoad(file.Resource);
            if (cached == null || cached.Stamp != file.Stamp || cached.BlockSize != _blockSize)
            {
                return false;
            }
            var keyName = file.Resource.KeyName;
            try
            {
                _store.IndexLeaves(file.Path, _blockSize, cached.Length, cached.Leaves);
                var record = cached.TryParseRecord();
                if (record != null && IsResumableLocked(record, keyName))
                {
                    await _node.ImportContentAsync(record, cached.Leaves, cancellationToken).ConfigureAwait(false);
                    _ledger.RecordPublished(keyName, record.Revision, record.FileHash);
                }
                else
                {
                    var revision = _ledger.NextRevision(keyName);
                    record = await _node.PublishAsync(keyName, cached.Leaves, cached.FileHash, cached.Length, revision, cancellationToken).ConfigureAwait(false);
                    _ledger.RecordPublished(keyName, revision, record.FileHash);
                    _leaves.Save(file.Resource, cached with { Record = record.ToBytes() });
                }
                _publications[keyName] = new Publication(file.Resource, record, file.Path, file.Stamp);
                _logger.LogInformation("Resumed {Key} revision {Revision} from its cached leaves without reading {Path}.", keyName, record.Revision, file.Path);
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                _logger.LogWarning(exception, "The cached leaves of {Key} were rejected: {Reason} The file is read and published again.", keyName, exception.Message);
                _store.Remove(file.Path);
                return false;
            }
        }

        /// <summary>
        /// Checks whether a cached record can be imported as it is: it is this publisher's publication of the key, the
        /// ledger knows no newer record, and it has more than half of its lifetime left.
        /// </summary>
        /// <param name="record">The cached record.</param>
        /// <param name="keyName">The key name of the resource.</param>
        /// <returns>Whether importing the record is preferable to signing a fresh revision.</returns>
        /// <remarks>The caller must hold <see cref="_sync"/>.</remarks>
        private bool IsResumableLocked(DHTRecord record, string keyName)
        {
            if (record.IsWithdrawn || record.Key.PublisherId != _node.NodeId || record.Key.Name != keyName)
            {
                return false;
            }
            if (_ledger.TryGet(keyName, out var entry) && (entry.Withdrawn || entry.Revision > record.Revision))
            {
                return false;
            }
            return record.ExpiresAt > DateTimeOffset.UtcNow + (_recordLifetime / 2);
        }

        /// <summary>Reads the current stamp of a file after it was published.</summary>
        /// <param name="path">The file path.</param>
        /// <returns>The stamp, or the default stamp when the file cannot be read any more, which matches no file.</returns>
        private static FileStamp TryReadStamp(string path)
        {
            try
            {
                return FileStamp.Read(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return default;
            }
        }

        /// <summary>Signs and replicates a withdrawal of a key and stops serving its file.</summary>
        /// <param name="keyName">The key name.</param>
        /// <param name="cancellationToken">Stops withdrawing.</param>
        /// <returns>Completion after the key was withdrawn.</returns>
        /// <exception cref="OperationCanceledException">Withdrawing was cancelled.</exception>
        /// <remarks>
        /// The caller must hold <see cref="_sync"/> and may run this for several different keys at once. Failures are logged;
        /// the next change or rescan retries.
        /// </remarks>
        private async Task WithdrawLockedAsync(string keyName, CancellationToken cancellationToken)
        {
            try
            {
                DHTRecord withdrawal;
                try
                {
                    // An explicit revision keeps withdrawals above the ledger even when the DHT is unreachable after a restart.
                    withdrawal = await _node.UnpublishAsync(keyName, _ledger.NextRevision(keyName), cancellationToken).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    // The DHT knows a higher revision than the ledger, for example after the ledger was lost.
                    withdrawal = await _node.UnpublishAsync(keyName, 0, cancellationToken).ConfigureAwait(false);
                }
                _ledger.RecordWithdrawn(keyName, withdrawal.Revision);
                if (_publications.TryRemove(keyName, out var publication))
                {
                    _store.Remove(publication.Path);
                }
                if (ChartResource.TryParseKeyName(keyName, out var resource))
                {
                    _leaves.Remove(resource);
                }
                _logger.LogInformation("Withdrew {Key} revision {Revision}.", keyName, withdrawal.Revision);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Withdrawing {Key} failed; it is retried on the next change or rescan.", keyName);
            }
        }

        /// <summary>Re-signs one publication under a higher revision, republishing the file if its content is no longer registered.</summary>
        /// <param name="publication">The publication to renew.</param>
        /// <param name="cancellationToken">Stops renewing.</param>
        /// <returns>Whether the publication was renewed.</returns>
        /// <exception cref="OperationCanceledException">Renewal was cancelled.</exception>
        /// <remarks>The caller must hold <see cref="_sync"/>, may run this for several publications at once, and saves the ledger.</remarks>
        private async Task<bool> RenewLockedAsync(Publication publication, CancellationToken cancellationToken)
        {
            var keyName = publication.Resource.KeyName;
            var revision = _ledger.NextRevision(keyName);
            try
            {
                var record = await _node.PublishAliasAsync(keyName, publication.Record, revision, cancellationToken).ConfigureAwait(false);
                _ledger.RecordPublished(keyName, revision, record.FileHash);
                _publications[keyName] = publication with { Record = record };
                _leaves.UpdateRecord(publication.Resource, record);
                _logger.LogDebug("Renewed {Key} with revision {Revision}.", keyName, revision);
                return true;
            }
            catch (ArgumentException)
            {
                return await RepublishLockedAsync(publication, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Renewing {Key} failed; it is retried at the next renewal check.", keyName);
                return false;
            }
        }

        /// <summary>Publishes a resource again from its file after the node forgot its content, for example because its record expired.</summary>
        /// <param name="publication">The publication whose content is no longer registered.</param>
        /// <param name="cancellationToken">Stops publishing.</param>
        /// <returns>Whether the resource was published again under a higher revision.</returns>
        /// <exception cref="OperationCanceledException">Publishing was cancelled.</exception>
        /// <remarks>The caller must hold <see cref="_sync"/>.</remarks>
        private async Task<bool> RepublishLockedAsync(Publication publication, CancellationToken cancellationToken)
        {
            var keyName = publication.Resource.KeyName;
            _logger.LogInformation("{Key} is no longer registered locally; publishing it again from {Path}.", keyName, publication.Path);
            // Forgetting the publication makes the next step read and sign the file again instead of reusing the record.
            _publications.TryRemove(keyName, out _);
            ChartFile? file;
            try
            {
                file = _charts.FindFile(publication.Resource);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Could not look for the file of {Key}; it is retried on the next change or rescan.", keyName);
                return false;
            }
            if (file == null)
            {
                _logger.LogInformation("{Key} has no file any more; it is withdrawn on the next rescan.", keyName);
                return false;
            }
            await PublishIfChangedLockedAsync(file.Value, cancellationToken).ConfigureAwait(false);
            return _publications.TryGetValue(keyName, out var republished) && republished.Record.Revision > publication.Record.Revision;
        }

        /// <summary>A publication this process signed and serves.</summary>
        /// <param name="Resource">The published resource.</param>
        /// <param name="Record">The latest signed record.</param>
        /// <param name="Path">The file that backs the publication.</param>
        /// <param name="Stamp">The file version that was published.</param>
        private sealed record Publication(ChartResource Resource, DHTRecord Record, string Path, FileStamp Stamp);
    }
}
