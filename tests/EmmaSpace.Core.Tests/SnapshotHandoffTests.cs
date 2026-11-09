using System.Diagnostics;
using EmmaSpace.Core.Platform;
using EmmaSpace.Core.Snapshots;

namespace EmmaSpace.Core.Tests;

public class SnapshotHandoffTests
{
    private static string TestPipe() => "EmmaSpace.Test." + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task A_running_app_receives_double_clicked_snapshots_in_order()
    {
        var pipe = TestPipe();
        var received = new List<string>();
        var both = new TaskCompletionSource();
        using var listener = SnapshotHandoff.Listen(path =>
        {
            lock (received)
            {
                received.Add(path);
                if (received.Count == 2) both.TrySetResult();
            }
        }, pipe);
        Assert.NotNull(listener);

        const string first = "/home/emma/Snapshots/Disk C, 2026 [weekly].emmasnap";
        const string second = @"C:\Users\Emma\Снимки\диск.emmasnap";
        Assert.True(SnapshotHandoff.TrySend(first, TimeSpan.FromSeconds(5), pipe));
        Assert.True(SnapshotHandoff.TrySend(second, TimeSpan.FromSeconds(5), pipe));
        await both.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal([first, second], received);
    }

    [Fact]
    public void Without_a_running_app_the_new_process_gives_up_quickly()
    {
        var stopwatch = Stopwatch.StartNew();
        Assert.False(SnapshotHandoff.TrySend("/tmp/a.emmasnap", TimeSpan.FromMilliseconds(300), TestPipe()));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void A_stopped_app_no_longer_answers()
    {
        var pipe = TestPipe();
        var listener = SnapshotHandoff.Listen(_ => { }, pipe);
        Assert.NotNull(listener);
        listener.Dispose();
        Assert.False(SnapshotHandoff.TrySend("/tmp/a.emmasnap", TimeSpan.FromMilliseconds(300), pipe));
    }

    [Fact]
    public void The_question_shows_what_a_snapshot_is_of_and_when_it_was_taken()
    {
        var file = Path.Combine(Path.GetTempPath(), $"emmaspace-{Guid.NewGuid():N}.emmasnap");
        try
        {
            using (var tree = TreeFixtures.Sample())
            using (var stream = File.Create(file))
                SnapshotFile.Save(tree, stream);
            var summary = SnapshotFile.TryReadSummary(file);
            Assert.NotNull(summary);
            Assert.Equal(@"C:\Root", summary.Value.RootPath);

            File.WriteAllText(file, "not a snapshot");
            Assert.Null(SnapshotFile.TryReadSummary(file));
            Assert.Null(SnapshotFile.TryReadSummary(file + ".missing"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
