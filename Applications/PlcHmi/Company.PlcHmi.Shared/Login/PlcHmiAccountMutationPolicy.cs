using Company.Platform.Shared.Login;

namespace Company.PlcHmi.Shared.Login;

/// <summary>PLC HMI 特有的人员锁保护。公共账户服务只调用策略，不引用维护锁实现。</summary>
public sealed class PlcHmiAccountMutationPolicy : IAccountMutationPolicy
{
    public void CheckDelete(UserAccountInfo target)
    {
        if(MaintenanceInterlock.Holds(target.Id))throw new InvalidOperationException("该用户仍持有维护锁，不能删除。");
    }
    public void CheckResetPassword(UserAccountInfo target)
    {
        if(MaintenanceInterlock.Holds(target.Id))throw new InvalidOperationException("该用户仍持有维护锁，不能由他人重置密码。");
    }
}
