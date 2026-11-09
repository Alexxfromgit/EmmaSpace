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
        Assert.False(SnapshotHandoff.TrySend("/tmp/a.emmasnap", TimeSpan.FromMilliseconds(150), TestPipe()));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task A_running_app_that_stalls_mid_exchange_is_given_up_on()
    {
        // Accepts the connection, then never sends its process id: the new process must not wait forever.
        var pipe = TestPipe();
        await using var server = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        var connected = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var sent = await Task.Run(() => SnapshotHandoff.TrySend("/tmp/a.emmasnap", TimeSpan.FromSeconds(5), pipe),
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.False(sent);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(12), $"took {stopwatch.Elapsed}");
        await connected;
    }

    [Fact]
    public void A_stopped_app_no_longer_answers()
    {
        var pipe = TestPipe();
        var listener = SnapshotHandoff.Listen(_ => { }, pipe);
        Assert.NotNull(listener);
        listener.Dispose();
        Assert.False(SnapshotHandoff.TrySend("/tmp/a.emmasnap", TimeSpan.FromMilliseconds(150), pipe));
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

            // A damaged root path length (an invalid 7-bit count, then one claiming ~2 GB) is reported as
            // a corrupt file, never as an unexpected exception or a huge read.
            byte[][] damaged = [[0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01], [0xFF, 0xFF, 0xFF, 0xFF, 0x07]];
            foreach (var length in damaged)
            {
                File.WriteAllBytes(file, [.. "EMSNAP01"u8, .. BitConverter.GetBytes(1), .. length, .. new byte[64]]);
                Assert.Null(SnapshotFile.TryReadSummary(file));
                using var stream = File.OpenRead(file);
                Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(stream));
            }
            Assert.Null(SnapshotFile.TryReadSummary(file + ".missing"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
