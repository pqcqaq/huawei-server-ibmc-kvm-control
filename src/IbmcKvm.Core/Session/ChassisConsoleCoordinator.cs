using System.Collections.Immutable;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Core.Session;

public sealed record ChassisConsoleSlot<TSession>(
    byte BladeNumber,
    ChassisBladeState DiscoveryState,
    KvmBladeSessionMode Mode,
    TSession Session)
    where TSession : class, IAsyncDisposable;

public sealed class ChassisConsoleCoordinator<TSession> : IAsyncDisposable
    where TSession : class, IAsyncDisposable
{
    public const int MaximumSessions = 4;

    private readonly Func<ChassisBladeState, KvmBladeSessionMode, CancellationToken, Task<TSession>> sessionFactory;
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly Dictionary<byte, ChassisConsoleSlot<TSession>> slots = [];
    private int disposed;

    public ChassisConsoleCoordinator(
        ChassisBladeState initialState,
        TSession initialSession,
        Func<ChassisBladeState, KvmBladeSessionMode, CancellationToken, Task<TSession>> sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(initialSession);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        this.sessionFactory = sessionFactory;
        slots.Add(
            initialState.BladeNumber,
            new ChassisConsoleSlot<TSession>(
                initialState.BladeNumber,
                initialState,
                KvmBladeSessionMode.Control,
                initialSession));
        SelectedBladeNumber = initialState.BladeNumber;
    }

    public byte? SelectedBladeNumber { get; private set; }

    public bool IsSplitViewEnabled { get; private set; }

    public ImmutableArray<ChassisConsoleSlot<TSession>> Sessions
    {
        get
        {
            lock (slots)
            {
                return slots.Values.OrderBy(slot => slot.BladeNumber).ToImmutableArray();
            }
        }
    }

    public ChassisConsoleSlot<TSession>? SelectedSession
    {
        get
        {
            lock (slots)
            {
                return SelectedBladeNumber is { } blade && slots.TryGetValue(blade, out var slot)
                    ? slot
                    : null;
            }
        }
    }

    public event EventHandler? Changed;

    public async Task<ChassisConsoleSlot<TSession>> ConnectAsync(
        ChassisBladeState state,
        KvmBladeSessionMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (slots)
            {
                if (slots.TryGetValue(state.BladeNumber, out var existing))
                {
                    SelectedBladeNumber = state.BladeNumber;
                    Changed?.Invoke(this, EventArgs.Empty);
                    return existing;
                }

                if (slots.Count >= MaximumSessions)
                {
                    throw new InvalidOperationException(
                        $"The console supports at most {MaximumSessions} simultaneous KVM sessions.");
                }
            }

            var session = await sessionFactory(state, mode, cancellationToken).ConfigureAwait(false);
            var slot = new ChassisConsoleSlot<TSession>(state.BladeNumber, state, mode, session);
            var retained = false;
            try
            {
                lock (slots)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                    slots.Add(state.BladeNumber, slot);
                    SelectedBladeNumber = state.BladeNumber;
                    retained = true;
                }

                Changed?.Invoke(this, EventArgs.Empty);
                return slot;
            }
            finally
            {
                if (!retained)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public void Select(byte bladeNumber)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (slots)
        {
            if (!slots.ContainsKey(bladeNumber))
            {
                throw new KeyNotFoundException($"Blade {bladeNumber} does not have an active console session.");
            }

            SelectedBladeNumber = bladeNumber;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetSplitView(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (IsSplitViewEnabled == enabled)
        {
            return;
        }

        IsSplitViewEnabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ReplaceSession(byte bladeNumber, TSession expected, TSession replacement)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (slots)
        {
            if (!slots.TryGetValue(bladeNumber, out var current) ||
                !ReferenceEquals(current.Session, expected))
            {
                throw new InvalidOperationException(
                    $"Blade {bladeNumber} no longer owns the session being replaced.");
            }

            slots[bladeNumber] = current with { Session = replacement };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask RouteToSelectedControlAsync(
        Func<TSession, CancellationToken, ValueTask> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var selected = SelectedSession ?? throw new InvalidOperationException("No blade is selected.");
        if (selected.Mode != KvmBladeSessionMode.Control)
        {
            throw new UnauthorizedAccessException("A monitor session is read-only.");
        }

        await command(selected.Session, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(byte bladeNumber, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ChassisConsoleSlot<TSession> removed;
            lock (slots)
            {
                if (!slots.Remove(bladeNumber, out removed!))
                {
                    return;
                }

                if (SelectedBladeNumber == bladeNumber)
                {
                    SelectedBladeNumber = slots.Keys.Order().Cast<byte?>().FirstOrDefault();
                }
            }

            await removed.Session.DisposeAsync().ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ChassisConsoleSlot<TSession>[] remaining;
            lock (slots)
            {
                remaining = slots.Values.ToArray();
                slots.Clear();
                SelectedBladeNumber = null;
            }

            List<Exception>? failures = null;
            foreach (var slot in remaining)
            {
                try
                {
                    await slot.Session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            if (failures is not null)
            {
                throw new AggregateException("One or more blade sessions could not be closed.", failures);
            }
        }
        finally
        {
            mutationGate.Release();
            mutationGate.Dispose();
        }
    }
}
