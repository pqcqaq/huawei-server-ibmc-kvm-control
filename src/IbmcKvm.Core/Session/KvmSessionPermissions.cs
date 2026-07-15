using IbmcKvm.Protocol.Profiles;

namespace IbmcKvm.Core.Session;

public enum KvmPrivilegeLevel
{
    Callback = 1,
    User = 2,
    Operator = 3,
    Administrator = 4,
}

public enum KvmPrivilegeOperation
{
    Power,
    VirtualMedia,
}

public sealed record KvmSessionPermissions(
    KvmPrivilegeLevel Privilege,
    bool CanControlKvm,
    bool CanControlPower,
    bool CanUseVirtualMedia)
{
    public static KvmSessionPermissions Create(
        int rawPrivilege,
        KvmProtocolCapabilities capabilities,
        bool powerDenied,
        bool virtualMediaDenied,
        bool readOnly = false)
    {
        if (rawPrivilege is < (int)KvmPrivilegeLevel.Callback or > (int)KvmPrivilegeLevel.Administrator)
        {
            throw new ArgumentOutOfRangeException(nameof(rawPrivilege));
        }

        var privilege = (KvmPrivilegeLevel)rawPrivilege;
        return new KvmSessionPermissions(
            privilege,
            !readOnly && privilege >= KvmPrivilegeLevel.User,
            !readOnly && privilege >= KvmPrivilegeLevel.Operator && capabilities.SupportsPowerControl && !powerDenied,
            !readOnly && privilege >= KvmPrivilegeLevel.User && capabilities.SupportsVirtualMedia && !virtualMediaDenied);
    }
}

public sealed class KvmPrivilegeDeniedEventArgs(
    KvmPrivilegeOperation operation,
    byte state) : EventArgs
{
    public KvmPrivilegeOperation Operation { get; } = operation;

    public byte State { get; } = state;
}
