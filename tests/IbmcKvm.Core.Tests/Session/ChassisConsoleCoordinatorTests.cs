using IbmcKvm.Core.Session;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Core.Tests.Session;

public sealed class ChassisConsoleCoordinatorTests
{
    [Fact]
    public async Task BoundsSessionsAtFourConnectionLimit()
    {
        var primary = new FakeSession(1);
        await using var coordinator = CreateCoordinator(primary);

        await coordinator.ConnectAsync(State(2), KvmBladeSessionMode.Control);
        await coordinator.ConnectAsync(State(3), KvmBladeSessionMode.Control);
        await coordinator.ConnectAsync(State(4), KvmBladeSessionMode.Monitor);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ConnectAsync(State(5), KvmBladeSessionMode.Control));

        Assert.Contains("at most 4", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, coordinator.Sessions.Select(slot => slot.BladeNumber));
    }

    [Fact]
    public async Task RoutesCommandsOnlyToSelectedControlSession()
    {
        var primary = new FakeSession(1);
        await using var coordinator = CreateCoordinator(primary);
        var second = await coordinator.ConnectAsync(State(2), KvmBladeSessionMode.Control);

        await coordinator.RouteToSelectedControlAsync((session, _) => session.RecordCommandAsync());
        coordinator.Select(1);
        await coordinator.RouteToSelectedControlAsync((session, _) => session.RecordCommandAsync());

        Assert.Equal(1, primary.CommandCount);
        Assert.Equal(1, second.Session.CommandCount);
    }

    [Fact]
    public async Task MonitorSessionsRemainReadOnlyAndCanAppearInSplitView()
    {
        await using var coordinator = CreateCoordinator(new FakeSession(1));
        await coordinator.ConnectAsync(State(2), KvmBladeSessionMode.Monitor);
        coordinator.SetSplitView(true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await coordinator.RouteToSelectedControlAsync((session, _) => session.RecordCommandAsync()));

        Assert.True(coordinator.IsSplitViewEnabled);
        Assert.Equal(KvmBladeSessionMode.Monitor, coordinator.SelectedSession?.Mode);
    }

    [Fact]
    public async Task DisconnectAndDisposeReleaseEveryOwnedSessionOnce()
    {
        var primary = new FakeSession(1);
        var created = new List<FakeSession>();
        var coordinator = new ChassisConsoleCoordinator<FakeSession>(
            State(1),
            primary,
            (state, _, _) =>
            {
                var session = new FakeSession(state.BladeNumber);
                created.Add(session);
                return Task.FromResult(session);
            });
        await coordinator.ConnectAsync(State(2), KvmBladeSessionMode.Control);
        await coordinator.ConnectAsync(State(3), KvmBladeSessionMode.Control);

        await coordinator.DisconnectAsync(2);
        await coordinator.DisposeAsync();

        Assert.Equal(1, primary.DisposeCount);
        Assert.All(created, session => Assert.Equal(1, session.DisposeCount));
    }

    [Fact]
    public async Task ReconnectReplacementChangesOwnershipWithoutDisposingEitherSession()
    {
        var previous = new FakeSession(1);
        var replacement = new FakeSession(1);
        var coordinator = CreateCoordinator(previous);

        coordinator.ReplaceSession(1, previous, replacement);

        Assert.Same(replacement, coordinator.SelectedSession?.Session);
        Assert.Equal(0, previous.DisposeCount);
        await coordinator.DisposeAsync();
        Assert.Equal(0, previous.DisposeCount);
        Assert.Equal(1, replacement.DisposeCount);
    }

    private static ChassisConsoleCoordinator<FakeSession> CreateCoordinator(FakeSession primary) =>
        new(
            State(1),
            primary,
            (state, _, _) => Task.FromResult(new FakeSession(state.BladeNumber)));

    private static ChassisBladeState State(byte bladeNumber) => new(
        bladeNumber,
        ChassisBladeStatus.Available,
        0xB0,
        0,
        System.Net.IPAddress.Loopback,
        7500,
        false,
        null,
        true);

    private sealed class FakeSession(byte bladeNumber) : IAsyncDisposable
    {
        public byte BladeNumber { get; } = bladeNumber;

        public int CommandCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask RecordCommandAsync()
        {
            CommandCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
