namespace IbmcKvm.Core.Session;

public sealed record KvmReconnectPolicy(
    int MaximumAttempts = 3,
    TimeSpan? MaximumElapsed = null,
    TimeSpan? InitialDelay = null,
    TimeSpan? MaximumDelay = null)
{
    public TimeSpan EffectiveMaximumElapsed { get; } = MaximumElapsed ?? TimeSpan.FromSeconds(30);

    public TimeSpan EffectiveInitialDelay { get; } = InitialDelay ?? TimeSpan.FromMilliseconds(250);

    public TimeSpan EffectiveMaximumDelay { get; } = MaximumDelay ?? TimeSpan.FromSeconds(5);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumAttempts, 1);
        if (EffectiveMaximumElapsed <= TimeSpan.Zero ||
            EffectiveInitialDelay < TimeSpan.Zero ||
            EffectiveMaximumDelay < EffectiveInitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumElapsed));
        }
    }
}

public sealed record KvmReconnectProgress(
    int Attempt,
    int MaximumAttempts,
    TimeSpan Elapsed,
    string State,
    Exception? Error = null);

public sealed class KvmReconnectException(string message, Exception? innerException = null)
    : IOException(message, innerException);
