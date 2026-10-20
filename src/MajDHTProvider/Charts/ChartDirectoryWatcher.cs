using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProvider.Charts
{
    /// <summary>
    /// Watches a chart root and reports each chart whose directory changed once its file events have been quiet for a
    /// while, so files that are still being copied are not processed half-written. A watcher buffer overflow and a periodic
    /// interval both request a full rescan, because file system notifications can be lost.
    /// </summary>
    internal sealed class ChartDirectoryWatcher : IDisposable
    {
        /// <summary>The shortest interval between checks for quiet charts.</summary>
        private static readonly TimeSpan s_minimumTick = TimeSpan.FromMilliseconds(50);
        /// <summary>The longest interval between checks for quiet charts.</summary>
        private static readonly TimeSpan s_maximumTick = TimeSpan.FromSeconds(1);

        /// <summary>The watched chart layout.</summary>
        private readonly ChartDirectory _charts;
        /// <summary>How long a chart must have no file events before it is reported.</summary>
        private readonly TimeSpan _quietPeriod;
        /// <summary>The interval between full rescans.</summary>
        private readonly TimeSpan _rescanInterval;
        /// <summary>Receives watcher diagnostics.</summary>
        private readonly ILogger _logger;
        /// <summary>The operating system notification source.</summary>
        private readonly FileSystemWatcher _watcher;
        /// <summary>Protects the pending changes.</summary>
        private readonly Lock _gate = new();
        /// <summary>The time of the latest file event of every changed chart that has not been reported yet.</summary>
        private readonly Dictionary<Guid, DateTimeOffset> _pending = [];
        /// <summary>Whether lost notifications require a full rescan.</summary>
        private bool _rescanRequested;

        /// <summary>Creates a watcher that is idle until <see cref="RunAsync"/> starts it.</summary>
        /// <param name="charts">The chart layout to watch.</param>
        /// <param name="quietPeriod">How long a chart must have no file events before it is reported.</param>
        /// <param name="rescanInterval">The interval between full rescans.</param>
        /// <param name="logger">Receives watcher diagnostics.</param>
        /// <exception cref="ArgumentOutOfRangeException">An interval is not positive.</exception>
        public ChartDirectoryWatcher(ChartDirectory charts, TimeSpan quietPeriod, TimeSpan rescanInterval, ILogger logger)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quietPeriod, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rescanInterval, TimeSpan.Zero);
            _charts = charts;
            _quietPeriod = quietPeriod;
            _rescanInterval = rescanInterval;
            _logger = logger;
            _watcher = new FileSystemWatcher(charts.RootPath)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
        }

        /// <summary>
        /// Watches the chart root until cancellation, invoking the callbacks one at a time. A callback failure is logged and
        /// does not stop watching; the next change or rescan retries the chart.
        /// </summary>
        /// <param name="onChartChanged">Synchronizes one chart whose directory changed.</param>
        /// <param name="onRescan">Synchronizes every chart.</param>
        /// <param name="cancellationToken">Stops watching.</param>
        /// <returns>A task that completes only by cancellation.</returns>
        /// <exception cref="OperationCanceledException">Watching was cancelled.</exception>
        public async Task RunAsync(Func<Guid, CancellationToken, Task> onChartChanged, Func<CancellationToken, Task> onRescan,
            CancellationToken cancellationToken)
        {
            _watcher.EnableRaisingEvents = true;
            _logger.LogInformation("Watching chart directory {Root}.", _charts.RootPath);
            var tick = TimeSpan.FromTicks(Math.Clamp(_quietPeriod.Ticks / 4, s_minimumTick.Ticks, s_maximumTick.Ticks));
            var nextRescan = DateTimeOffset.UtcNow + _rescanInterval;
            try
            {
                while (true)
                {
                    await Task.Delay(tick, cancellationToken).ConfigureAwait(false);
                    var now = DateTimeOffset.UtcNow;
                    var (charts, rescan) = TakeDue(now);
                    if (rescan || now >= nextRescan)
                    {
                        _logger.LogDebug("Rescanning chart directory {Root}.", _charts.RootPath);
                        await InvokeAsync(() => onRescan(cancellationToken), "all charts").ConfigureAwait(false);
                        nextRescan = DateTimeOffset.UtcNow + _rescanInterval;
                        continue;
                    }
                    foreach (var chartId in charts)
                    {
                        _logger.LogDebug("Chart directory {ChartId} changed.", chartId);
                        await InvokeAsync(() => onChartChanged(chartId, cancellationToken), chartId.ToString()).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _watcher.EnableRaisingEvents = false;
            }
        }

        /// <summary>Stops and releases the notification source.</summary>
        public void Dispose()
        {
            _watcher.Dispose();
        }

        /// <summary>Takes the charts whose events have been quiet long enough, and any rescan request.</summary>
        /// <param name="now">The current time.</param>
        /// <returns>The charts to report and whether a full rescan was requested; a rescan supersedes every pending chart.</returns>
        private (List<Guid> Charts, bool Rescan) TakeDue(DateTimeOffset now)
        {
            var due = new List<Guid>();
            lock (_gate)
            {
                if (_rescanRequested)
                {
                    _rescanRequested = false;
                    _pending.Clear();
                    return (due, true);
                }
                foreach (var (chartId, lastEvent) in _pending)
                {
                    if (now - lastEvent >= _quietPeriod)
                    {
                        due.Add(chartId);
                    }
                }
                foreach (var chartId in due)
                {
                    _pending.Remove(chartId);
                }
            }
            return (due, false);
        }

        /// <summary>Runs a callback, logging any failure other than cancellation.</summary>
        /// <param name="callback">The synchronization callback.</param>
        /// <param name="target">Describes the synchronized charts in logs.</param>
        /// <returns>Completion of the callback.</returns>
        /// <exception cref="OperationCanceledException">The callback was cancelled.</exception>
        private async Task InvokeAsync(Func<Task> callback, string target)
        {
            try
            {
                await callback().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Synchronizing {Target} after a directory change failed; it is retried on the next change or rescan.", target);
            }
        }

        /// <summary>Records a change of a file or directory.</summary>
        /// <param name="fullPath">The changed path.</param>
        private void Mark(string fullPath)
        {
            if (!_charts.TryGetChartId(fullPath, out var chartId))
            {
                return;
            }
            lock (_gate)
            {
                _pending[chartId] = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>Handles created, changed and deleted notifications.</summary>
        /// <param name="sender">The watcher.</param>
        /// <param name="e">The changed path.</param>
        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            Mark(e.FullPath);
        }

        /// <summary>Handles a rename, which changes both the old and the new chart.</summary>
        /// <param name="sender">The watcher.</param>
        /// <param name="e">The old and new paths.</param>
        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Mark(e.OldFullPath);
            Mark(e.FullPath);
        }

        /// <summary>Requests a full rescan after notifications were lost.</summary>
        /// <param name="sender">The watcher.</param>
        /// <param name="e">The watcher failure.</param>
        private void OnError(object sender, ErrorEventArgs e)
        {
            _logger.LogWarning(e.GetException(), "Chart directory notifications were lost; a full rescan is scheduled.");
            lock (_gate)
            {
                _rescanRequested = true;
            }
        }
    }
}
