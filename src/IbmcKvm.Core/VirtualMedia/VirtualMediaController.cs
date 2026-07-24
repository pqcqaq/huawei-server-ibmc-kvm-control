using IbmcKvm.Core.Session;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Core.VirtualMedia;

public sealed record VirtualMediaSlotStatus(
    MediaDeviceKind DeviceKind,
    bool IsMounted,
    string? DisplayName,
    bool IsReadOnly,
    string State);

public sealed record VirtualMediaCapability(bool Available, int Port, KvmCipherSuite CipherSuite);

public sealed class VirtualMediaController : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<MediaDeviceKind, IRandomAccessMedia> media = [];
    private VirtualMediaSession? session;
    private KvmClientSession kvmSession;
    private int disposed;

    public VirtualMediaController(KvmClientSession kvmSession)
    {
        this.kvmSession = kvmSession ?? throw new ArgumentNullException(nameof(kvmSession));
    }

    public event EventHandler<VirtualMediaSlotStatus>? StatusChanged;

    public IReadOnlyList<PhysicalDriveDescriptor> EnumeratePhysicalDrives()
    {
        ThrowIfDisposed();
        return PhysicalDriveMedia.Enumerate();
    }

    public async Task<VirtualMediaCapability> QueryCapabilityAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = await kvmSession.GetVirtualMediaEndpointAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new VirtualMediaCapability(true, endpoint.Port, endpoint.CipherSuite);
    }

    public Task MountImageAsync(
        MediaDeviceKind kind,
        string imagePath,
        bool writeProtected = true,
        CancellationToken cancellationToken = default)
    {
        var mounted = new FileImageMedia(
            imagePath,
            kind,
            isReadOnly: kind == MediaDeviceKind.Optical || writeProtected);
        return MountOwnedAsync(mounted, cancellationToken);
    }

    public async Task MountDirectoryAsync(
        string directory,
        IProgress<MediaBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var mounted = await DirectoryIsoMedia.CreateAsync(
            directory,
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await MountOwnedAsync(mounted, cancellationToken).ConfigureAwait(false);
    }

    public Task MountPhysicalAsync(
        PhysicalDriveDescriptor descriptor,
        bool writeProtected = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var writable = descriptor.DeviceKind == MediaDeviceKind.Floppy && !writeProtected;
        return MountOwnedAsync(PhysicalDriveMedia.Open(descriptor, writable), cancellationToken);
    }

    public async Task CreateImageAsync(
        PhysicalDriveDescriptor source,
        string destinationPath,
        IProgress<MediaCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        await using var drive = PhysicalDriveMedia.Open(source);
        await MediaImageCreator.CreateAsync(drive, destinationPath, progress, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task EjectAsync(MediaDeviceKind kind, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!media.Remove(kind, out var mounted))
            {
                return;
            }

            try
            {
                if (session is not null)
                {
                    await session.EjectAsync(kind, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await mounted.DisposeAsync().ConfigureAwait(false);
                OnStatus(new(kind, false, null, true, "未挂载"));
            }

            if (media.Count == 0 && session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                session = null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                session = null;
            }

            if (media.Count == 0)
            {
                return;
            }

            session = await ConnectSessionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var mounted in media.Values)
            {
                await session.MountAsync(mounted, cancellationToken).ConfigureAwait(false);
                OnStatus(StatusFor(mounted, "已重新连接"));
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReplaceKvmSessionAsync(
        KvmClientSession replacement,
        bool restoreMountedMedia,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ThrowIfDisposed();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                session = null;
            }

            kvmSession = replacement;
            if (!restoreMountedMedia || media.Count == 0)
            {
                return;
            }

            try
            {
                session = await ConnectSessionAsync(cancellationToken).ConfigureAwait(false);
                foreach (var mounted in media.Values)
                {
                    await session.MountAsync(mounted, cancellationToken).ConfigureAwait(false);
                    OnStatus(StatusFor(mounted, "已恢复连接"));
                }
            }
            catch
            {
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    session = null;
                }

                foreach (var mounted in media.Values)
                {
                    OnStatus(StatusFor(mounted, "等待重新连接"));
                }

                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ResetUsbAsync(bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed)
        {
            throw new InvalidOperationException("USB reset requires explicit user confirmation.");
        }

        await kvmSession.SendPowerAsync(KvmPowerAction.UsbReset, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                session = null;
            }

            foreach (var mounted in media.Values)
            {
                await mounted.DisposeAsync().ConfigureAwait(false);
            }

            media.Clear();
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private async Task MountOwnedAsync(IRandomAccessMedia mounted, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var kind = mounted.DeviceKind;
            if (media.Remove(kind, out var previous))
            {
                if (session is not null)
                {
                    await session.EjectAsync(kind, cancellationToken).ConfigureAwait(false);
                }

                await previous.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                session ??= await ConnectSessionAsync(cancellationToken).ConfigureAwait(false);
                await session.MountAsync(mounted, cancellationToken).ConfigureAwait(false);
                media.Add(kind, mounted);
                OnStatus(StatusFor(mounted, "已挂载"));
            }
            catch
            {
                await mounted.DisposeAsync().ConfigureAwait(false);
                OnStatus(new(kind, false, null, true, "挂载失败"));
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<VirtualMediaSession> ConnectSessionAsync(CancellationToken cancellationToken)
    {
        var endpoint = await kvmSession.GetVirtualMediaEndpointAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return await VirtualMediaSession.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    private void OnStatus(VirtualMediaSlotStatus status) => StatusChanged?.Invoke(this, status);

    private static VirtualMediaSlotStatus StatusFor(IRandomAccessMedia mounted, string state) =>
        new(mounted.DeviceKind, true, mounted.DisplayName, mounted.IsReadOnly, state);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
