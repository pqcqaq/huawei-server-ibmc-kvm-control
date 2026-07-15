namespace IbmcKvm.Protocol.Session;

public interface IKvmPayloadCryptography : IDisposable
{
    bool IsSessionEstablished { get; }

    byte[] EncryptInput(ReadOnlySpan<byte> input);

    byte[] EncryptPower(KvmPowerAction action);

    byte[] DecryptData(ReadOnlySpan<byte> ciphertext, int realLength);
}
