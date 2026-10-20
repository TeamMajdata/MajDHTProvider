using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MajDHTProvider.Charts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>Checks that directory changes are reported per chart after a quiet period.</summary>
    public sealed class ChartDirectoryWatcherTests
    {
        /// <summary>Reports created, changed and deleted chart directories, ignoring paths outside chart directories.</summary>
        /// <returns>Completion after the notifications.</returns>
        [Fact]
        public async Task ChangesAreReportedPerChart()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var root = new TestDirectory();
            var charts = new ChartDirectory(root.Path);
            using var watcher = new ChartDirectoryWatcher(charts, TimeSpan.FromMilliseconds(200), TimeSpan.FromHours(1), NullLogger.Instance);
            var changed = new ConcurrentQueue<Guid>();
            var rescans = 0;
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var watching = watcher.RunAsync(
                (chartId, _) =>
                {
                    changed.Enqueue(chartId);
                    return Task.CompletedTask;
                },
                _ =>
                {
                    Interlocked.Increment(ref rescans);
                    return Task.CompletedTask;
                },
                stopping.Token);
            // Give the watcher time to subscribe before the first change.
            await Task.Delay(300, deadline.Token);
            var chartId = Guid.NewGuid();
            var resource = new ChartResource(chartId, ChartResourceKind.Maidata);
            TestSupport.WriteResource(root.Path, resource, 100);
            Directory.CreateDirectory(root.Combine("not-a-chart"));
            File.WriteAllText(root.Combine("not-a-chart", "maidata.txt"), "ignored");

            await TestSupport.WaitUntilAsync(() => Task.FromResult(changed.ToArray().Contains(chartId)), deadline.Token);
            // Several events of one burst are reported once.
            await Task.Delay(500, deadline.Token);
            Assert.Single(changed, id => id == chartId);
            Assert.All(changed, id => Assert.Equal(chartId, id));

            changed.Clear();
            Directory.Delete(Path.GetDirectoryName(TestSupport.ResourcePath(root.Path, resource))!, true);
            await TestSupport.WaitUntilAsync(() => Task.FromResult(changed.ToArray().Contains(chartId)), deadline.Token);
            Assert.Equal(0, Volatile.Read(ref rescans));

            await stopping.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
        }

        /// <summary>Requests a full rescan at the rescan interval.</summary>
        /// <returns>Completion after a rescan.</returns>
        [Fact]
        public async Task RescansRunPeriodically()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var root = new TestDirectory();
            using var watcher = new ChartDirectoryWatcher(new ChartDirectory(root.Path), TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(300), NullLogger.Instance);
            var rescanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var watching = watcher.RunAsync((_, _) => Task.CompletedTask,
                _ =>
                {
                    rescanned.TrySetResult();
                    return Task.CompletedTask;
                },
                stopping.Token);
            await rescanned.Task.WaitAsync(deadline.Token);
            await stopping.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
        }

        /// <summary>Keeps watching after a synchronization callback fails.</summary>
        /// <returns>Completion after the second notification.</returns>
        [Fact]
        public async Task CallbackFailuresDoNotStopWatching()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var root = new TestDirectory();
            using var watcher = new ChartDirectoryWatcher(new ChartDirectory(root.Path), TimeSpan.FromMilliseconds(100),
                TimeSpan.FromHours(1), NullLogger.Instance);
            var calls = 0;
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var watching = watcher.RunAsync(
                (_, _) =>
                {
                    Interlocked.Increment(ref calls);
                    throw new IOException("Simulated synchronization failure.");
                },
                _ => Task.CompletedTask,
                stopping.Token);
            await Task.Delay(300, deadline.Token);
            TestSupport.WriteResource(root.Path, new ChartResource(Guid.NewGuid(), ChartResourceKind.Image), 10);
            await TestSupport.WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref calls) >= 1), deadline.Token);
            TestSupport.WriteResource(root.Path, new ChartResource(Guid.NewGuid(), ChartResourceKind.Image), 10);
            await TestSupport.WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref calls) >= 2), deadline.Token);
            await stopping.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
        }
    }
}
