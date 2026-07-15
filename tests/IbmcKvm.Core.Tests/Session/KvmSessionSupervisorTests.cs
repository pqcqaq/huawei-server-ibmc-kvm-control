using IbmcKvm.Core.Session;

namespace IbmcKvm.Core.Tests.Session;

public sealed class KvmSessionSupervisorTests
{
    [Fact]
    public async Task RetriesWithinBoundAndRestoresMediaAfterConnection()
    {
        var supervisor = new KvmSessionSupervisor(new KvmReconnectPolicy(
            MaximumAttempts: 3,
            MaximumElapsed: TimeSpan.FromSeconds(2),
            InitialDelay: TimeSpan.Zero,
            MaximumDelay: TimeSpan.Zero));
        var attempts = 0;
        var restored = 0;
        var states = new List<string>();
        supervisor.ProgressChanged += (_, progress) => states.Add(progress.State);

        var result = await supervisor.ReconnectAsync(
            new byte[128],
            (token, _) =>
            {
                Assert.Equal(128, token.Length);
                attempts++;
                return attempts < 3
                    ? Task.FromException<int>(new IOException("transient"))
                    : Task.FromResult(42);
            },
            (_, _) =>
            {
                restored++;
                return Task.CompletedTask;
            });

        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
        Assert.Equal(1, restored);
        Assert.Contains("restoring-media", states);
        Assert.Equal("connected", states[^1]);
    }

    [Fact]
    public async Task StopsAtAttemptLimitAndReportsLastFailure()
    {
        var supervisor = new KvmSessionSupervisor(new KvmReconnectPolicy(
            MaximumAttempts: 2,
            MaximumElapsed: TimeSpan.FromSeconds(1),
            InitialDelay: TimeSpan.Zero,
            MaximumDelay: TimeSpan.Zero));

        var attempts = 0;
        var exception = await Assert.ThrowsAsync<KvmReconnectException>(() => supervisor.ReconnectAsync(
            ReadOnlyMemory<byte>.Empty,
            (_, _) =>
            {
                attempts++;
                return Task.FromException<int>(new UnauthorizedAccessException("denied"));
            }));

        Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancellationDoesNotBecomeRetryFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var supervisor = new KvmSessionSupervisor(new KvmReconnectPolicy(
            MaximumAttempts: 3,
            InitialDelay: TimeSpan.FromSeconds(1)));

        await Assert.ThrowsAsync<OperationCanceledException>(() => supervisor.ReconnectAsync(
            new byte[128],
            (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(1);
            },
            cancellationToken: cancellation.Token));
    }
}
