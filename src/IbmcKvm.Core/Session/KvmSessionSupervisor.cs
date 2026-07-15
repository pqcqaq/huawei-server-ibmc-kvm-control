using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace IbmcKvm.Core.Session;

public sealed class KvmSessionSupervisor(KvmReconnectPolicy? policy = null)
{
    private readonly KvmReconnectPolicy policy = policy ?? new();

    public event EventHandler<KvmReconnectProgress>? ProgressChanged;

    public async Task<T> ReconnectAsync<T>(
        ReadOnlyMemory<byte> reconnectToken,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<T>> connect,
        Func<T, CancellationToken, Task>? restoreVirtualMedia = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connect);
        if (reconnectToken.Length is not (0 or 128))
        {
            throw new ArgumentException("A reconnect token must contain exactly 128 bytes.", nameof(reconnectToken));
        }

        policy.Validate();
        var stopwatch = Stopwatch.StartNew();
        Exception? lastError = null;
        var delay = policy.EffectiveInitialDelay;
        var tokenCopy = reconnectToken.ToArray();
        try
        {
            for (var attempt = 1; attempt <= policy.MaximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed >= policy.EffectiveMaximumElapsed)
                {
                    break;
                }

                Publish(new(attempt, policy.MaximumAttempts, stopwatch.Elapsed, "connecting"));
                try
                {
                    var session = await connect(tokenCopy, cancellationToken).ConfigureAwait(false);
                    if (restoreVirtualMedia is not null)
                    {
                        Publish(new(attempt, policy.MaximumAttempts, stopwatch.Elapsed, "restoring-media"));
                        await restoreVirtualMedia(session, cancellationToken).ConfigureAwait(false);
                    }

                    Publish(new(attempt, policy.MaximumAttempts, stopwatch.Elapsed, "connected"));
                    return session;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    Publish(new(attempt, policy.MaximumAttempts, stopwatch.Elapsed, "retrying", exception));
                    if (!IsTransient(exception))
                    {
                        break;
                    }
                }

                if (attempt == policy.MaximumAttempts)
                {
                    break;
                }

                var remaining = policy.EffectiveMaximumElapsed - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(delay <= remaining ? delay : remaining, cancellationToken).ConfigureAwait(false);
                delay = delay == TimeSpan.Zero
                    ? policy.EffectiveInitialDelay
                    : TimeSpan.FromMilliseconds(Math.Min(
                        policy.EffectiveMaximumDelay.TotalMilliseconds,
                        delay.TotalMilliseconds * 2));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenCopy);
        }

        throw new KvmReconnectException(
            $"The KVM reconnect policy exhausted {policy.MaximumAttempts} attempts.",
            lastError);
    }

    private void Publish(KvmReconnectProgress progress) => ProgressChanged?.Invoke(this, progress);

    private static bool IsTransient(Exception exception) => exception is
        IOException or
        SocketException or
        TimeoutException;
}
