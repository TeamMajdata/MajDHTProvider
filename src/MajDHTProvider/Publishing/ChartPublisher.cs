using System;
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
    /// their signed lifetime ends. All changes are serialized, so a directory event and a rescan never race.
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
        /// <summary>The block size the node signs publications with.</summary>
        private readonly int _blockSize;
        /// <summary>The signed lifetime of a publication.</summary>
        private readonly TimeSpan _recordLifetime;
        /// <summary>Receives publisher diagnostics.</summary>
        private readonly ILogger _logger;
        /// <summary>Serializes every change of the publications.</summary>
        private readonly SemaphoreSlim _sync = new(1, 1);
        /// <summary>The publications this process signed and serves, keyed by key name.</summary>
        private readonly Dictionary<string, Publication> _publications = new(StringComparer.Ordinal);

        /// <summary>Creates a publisher.</summary>
        /// <param name="node">The started node that signs and serves publications.</param>
        /// <param name="charts">The chart layout being published.</param>
        /// <param name="store">The block store the node was created with.</param>
        /// <param name="ledger">The persistent revisions of every signed key.</param>
        /// <param name="nodeOptions">The options the node was created with.</param>
        /// <param name="logger">Receives publisher diagnostics.</param>
        public ChartPublisher(MajDHTNode node, ChartDirectory charts, FileBlockStore store, PublicationLedger ledger,
            DHTNodeOptions nodeOptions, ILogger logger)
        {
            _node = node;
            _charts = charts;
            _store = store;
            _ledger = ledger;
            _blockSize = nodeOptions.BlockSize;
            _recordLifetime = nodeOptions.RecordLifetime;
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
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in _charts.Scan())
                {
                    present.Add(file.Resource.KeyName);
                    await PublishIfChangedLockedAsync(file, cancellationToken).ConfigureAwait(false);
                }
                var signed = new HashSet<string>(_ledger.PublishedKeyNames, StringComparer.Ordinal);
                signed.UnionWith(_publications.Keys);
                foreach (var keyName in signed)
                {
                    if (!present.Contains(keyName))
                    {
                        await WithdrawLockedAsync(keyName, cancellationToken).ConfigureAwait(false);
                    }
                }
                _logger.LogInformation("Publishing {Count} chart resources from {Root}.", _publications.Count, _charts.RootPath);
            }
            finally
            {
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
                var present = new HashSet<ChartResourceKind>();
                foreach (var file in _charts.ScanChart(chartId))
                {
                    present.Add(file.Resource.Kind);
                    await PublishIfChangedLockedAsync(file, cancellationToken).ConfigureAwait(false);
                }
                foreach (var kind in ChartResource.AllKinds)
                {
                    var keyName = new ChartResource(chartId, kind).KeyName;
                    if (!present.Contains(kind) && IsPublishedLocked(keyName))
                    {
                        await WithdrawLockedAsync(keyName, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
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
                foreach (var publication in due)
                {
                    if (await RenewLockedAsync(publication, cancellationToken).ConfigureAwait(false))
                    {
                        renewed++;
                    }
                }
                if (due.Count != 0)
                {
                    _ledger.Save();
                    _logger.LogInformation("Renewed {Renewed} of {Due} publications that reached half of their lifetime.", renewed, due.Count);
                }
                return renewed;
            }
            finally
            {
                _sync.Release();
            }
        }

        /// <summary>Checks whether the latest signed record of a key is a publication.</summary>
        /// <param name="keyName">The key name.</param>
        /// <returns>Whether the key is published.</returns>
        /// <remarks>The caller must hold <see cref="_sync"/>.</remarks>
        private bool IsPublishedLocked(string keyName)
        {
            return _publications.ContainsKey(keyName) || (_ledger.TryGet(keyName, out var entry) && !entry.Withdrawn);
        }

        /// <summary>Publishes a resource file unless the same content is already published from it.</summary>
        /// <param name="file">The resource file.</param>
        /// <param name="cancellationToken">Stops publishing.</param>
        /// <returns>Completion after the resource was published or skipped.</returns>
        /// <exception cref="OperationCanceledException">Publishing was cancelled.</exception>
        /// <remarks>The caller must hold <see cref="_sync"/>. Failures are logged; the next change or rescan retries.</remarks>
        private async Task PublishIfChangedLockedAsync(ChartFile file, CancellationToken cancellationToken)
        {
            var keyName = file.Resource.KeyName;
            try
            {
                var stamp = FileStamp.Read(file.Path);
                _publications.TryGetValue(keyName, out var existing);
                if (existing != null && existing.Path == file.Path && existing.Stamp == stamp)
                {
                    return;
                }
                var digest = await _store.IndexFileAsync(file.Path, _blockSize, cancellationToken).ConfigureAwait(false);
                if (existing != null && existing.Record.FileHash == digest.FileHash)
                {
                    _publications[keyName] = existing with { Path = file.Path, Stamp = stamp };
                    return;
                }
                var revision = _ledger.NextRevision(keyName);
                DHTRecord record;
                await using (var source = File.OpenRead(file.Path))
                {
                    record = await _node.PublishAsync(keyName, source, revision, cancellationToken).ConfigureAwait(false);
                }
                var unchangedDuringPublish = record.FileHash == digest.FileHash;
                if (!unchangedDuringPublish)
                {
                    _logger.LogWarning("{Key} changed while it was published; it is published again on the next change or rescan.", keyName);
                }
                _ledger.RecordPublished(keyName, revision, record.FileHash);
                _ledger.Save();
                // A default stamp never matches a real file, so content that changed during publishing is indexed again.
                _publications[keyName] = new Publication(file.Resource, record, file.Path, unchangedDuringPublish ? stamp : default);
                _logger.LogInformation("Published {Key} revision {Revision} ({Length} bytes).", keyName, revision, record.Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not read {Path} to publish {Key}; it is retried on the next change or rescan.", file.Path, keyName);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Publishing {Key} from {Path} failed.", keyName, file.Path);
            }
        }

        /// <summary>Signs and replicates a withdrawal of a key and stops serving its file.</summary>
        /// <param name="keyName">The key name.</param>
        /// <param name="cancellationToken">Stops withdrawing.</param>
        /// <returns>Completion after the key was withdrawn.</returns>
        /// <exception cref="OperationCanceledException">Withdrawing was cancelled.</exception>
        /// <remarks>The caller must hold <see cref="_sync"/>. Failures are logged; the next change or rescan retries.</remarks>
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
                _ledger.Save();
                if (_publications.Remove(keyName, out var publication))
                {
                    _store.Remove(publication.Path);
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
        /// <remarks>The caller must hold <see cref="_sync"/> and save the ledger.</remarks>
        private async Task<bool> RenewLockedAsync(Publication publication, CancellationToken cancellationToken)
        {
            var keyName = publication.Resource.KeyName;
            var revision = _ledger.NextRevision(keyName);
            try
            {
                var record = await _node.PublishAliasAsync(keyName, publication.Record, revision, cancellationToken).ConfigureAwait(false);
                _ledger.RecordPublished(keyName, revision, record.FileHash);
                _publications[keyName] = publication with { Record = record };
                _logger.LogDebug("Renewed {Key} with revision {Revision}.", keyName, revision);
                return true;
            }
            catch (ArgumentException)
            {
                _logger.LogInformation("{Key} is no longer registered locally; publishing it again from {Path}.", keyName, publication.Path);
                // Forgetting the publication makes the next step read and sign the file again instead of reusing the record.
                _publications.Remove(keyName);
                await PublishIfChangedLockedAsync(new ChartFile(publication.Resource, publication.Path), cancellationToken).ConfigureAwait(false);
                return _publications.TryGetValue(keyName, out var republished) && republished.Record.Revision > publication.Record.Revision;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Renewing {Key} failed; it is retried at the next renewal check.", keyName);
                return false;
            }
        }

        /// <summary>A publication this process signed and serves.</summary>
        /// <param name="Resource">The published resource.</param>
        /// <param name="Record">The latest signed record.</param>
        /// <param name="Path">The file that backs the publication.</param>
        /// <param name="Stamp">The file version that was published.</param>
        private sealed record Publication(ChartResource Resource, DHTRecord Record, string Path, FileStamp Stamp);
    }
}
