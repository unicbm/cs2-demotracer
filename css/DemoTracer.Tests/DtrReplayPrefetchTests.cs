namespace DemoTracer.Tests;

public sealed class DtrReplayPrefetchTests : IDisposable
{
    private readonly string tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"demotracer-prefetch-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task TryTakeNeverWaitsForPendingDecode()
    {
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, "pending.dtr");
        File.WriteAllBytes(path, [1, 2, 3]);

        using var enteredReader = new ManualResetEventSlim();
        using var releaseReader = new ManualResetEventSlim();
        var prefetch = new DtrReplayPrefetch(_ =>
        {
            enteredReader.Set();
            releaseReader.Wait();
            return default;
        });

        try
        {
            prefetch.Begin([path]);
            Assert.True(enteredReader.Wait(TimeSpan.FromSeconds(5)));

            var takeTask = Task.Run(() => prefetch.TryTake(path, out _));
            var completed = await Task.WhenAny(
                takeTask,
                Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.Same(takeTask, completed);
            Assert.Equal(
                DtrReplayPrefetchTakeStatus.Pending,
                await takeTask);
        }
        finally
        {
            releaseReader.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            prefetch.AllPendingCompleted,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(
            DtrReplayPrefetchTakeStatus.Failed,
            prefetch.TryTake(path, out _));
        Assert.Equal(
            DtrReplayPrefetchTakeStatus.Missing,
            prefetch.TryTake(path, out _));
        prefetch.Cancel();
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
    }
}
