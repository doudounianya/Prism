using Company.Application.Share.Login;
using Company.Application.Share.Model;
using Company.Core.Cache;
using Company.Core.Helper;
using Company.Core.Ioc;
using Company.Database;
using Company.Logger;

namespace Company.Application.Login.Services;

/// <summary>
/// 封装账号认证、管理员用户维护及本人改密，复用现有 Member 表和密码摘要格式。
/// 每次数据库操作在后台串行执行；管理操作会重新读取操作者的数据库记录来校验权限，
/// 不把界面按钮的可用状态当作授权依据。数据库修改成功后按账号清理记住登录的缓存。
/// </summary>
[ExposedService(Lifetime.Singleton, typeof(IUserAccountService))]
public sealed class UserAccountService(Func<IMemberRepository> repository, ISession session, ICacheManager cache) : IUserAccountService
{
    // 同一单例内的请求共用此门闩，避免账号写入、管理员数量检查与列表查询彼此交错。
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>统一在后台创建仓储并执行操作，成功、异常时均释放当前服务的串行入口。</summary>
    private async Task<T> Run<T>(Func<IMemberRepository, T> action)
    {
        // 等待门闩时不占用 UI 线程；repository() 也置于 Task.Run 内，覆盖仓储构造阶段的数据库访问。
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => action(repository())).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    // 对外投影只返回列表/会话所需的身份信息，避免把数据库密码摘要传给调用方。
    private static UserAccountInfo Info(Member m) => new(m.Id, m.UserName, m.Role, m.InsertDate);
    private Member Administrator(IMemberRepository repo)
    {
        var current = session.CurrentUser;
        // 同时比对数据库角色与用户名；会话里的角色可能已经过时，不能单凭缓存身份授权。
        var stored = current is { Id: > 0 } ? repo.GetEntity(current.Id) : null;
        if (stored is null || stored.Role != (int)UserRole.Administrator ||
            !string.Equals(stored.UserName, current!.UserName, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("只有管理员可以管理用户。");
        return stored;
    }
    // 用户名先去首尾空白再限制长度及控制字符；不改动名称内部字符。
    private static string UserName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 50 || name.Any(char.IsControl))
            throw new ArgumentException("用户名长度为 1～50 个字符，不能包含控制字符。");
        return name;
    }
    // 新密码校验通过后沿用既有 32 位 MD5 格式；这里不裁剪密码，空格仍是密码内容。
    private static string Password(string password, string confirmation)
    {
        if (password.Length is < 6 or > 128) throw new ArgumentException("新密码长度为 6～128 个字符。");
        if (password != confirmation) throw new ArgumentException("两次输入的新密码不一致。");
        return MD5Helper.GetMD5_32(password);
    }
    /// <summary>按完整用户名和既有密码摘要格式认证；输入为空或认证不通过时返回空身份。</summary>
    /// <remarks>仓储异常继续传播，由登录界面区分认证失败和数据库访问失败。</remarks>
    public Task<UserAccountInfo?> AuthenticateAsync(string userName, string password) => Run<UserAccountInfo?>(repo =>
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)) return null;
        return repo.Login(userName.Trim(), MD5Helper.GetMD5_32(password), out var member) ? Info(member) : null;
    });
    /// <summary>在重新确认管理员身份后返回按用户名排序的账号信息，不返回密码摘要。</summary>
    public Task<IReadOnlyList<UserAccountInfo>> GetUsersAsync() => Run<IReadOnlyList<UserAccountInfo>>(repo =>
    {
        Administrator(repo);
        return repo.GetAll().OrderBy(m => m.UserName, StringComparer.OrdinalIgnoreCase).Select(Info).ToArray();
    });
    /// <summary>校验管理员权限、账号唯一性、新密码和角色后创建用户。</summary>
    public Task CreateUserAsync(string userName, string password, string confirmation, UserRole role) => Run(repo =>
    {
        Administrator(repo);
        var name = UserName(userName); var hash = Password(password, confirmation);
        if (!Enum.IsDefined(role)) throw new ArgumentException("请选择有效的用户类别。");
        // 创建时按忽略大小写判断重名，防止仅大小写不同的名称造成管理和缓存匹配歧义。
        if (repo.GetAll().Any(m => string.Equals(m.UserName, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("用户名已存在。");
        if (!repo.Insert(new Member { UserName = name, Password = hash, Role = (int)role, InsertDate = DateTime.Now }))
            throw new InvalidOperationException("创建用户失败，请刷新列表后重试。");
        return true;
    });
    /// <summary>由管理员删除其他账号，保留当前操作者及最后一个管理员。</summary>
    public Task DeleteUserAsync(int userId) => Run(repo =>
    {
        var actor = Administrator(repo);
        var target = repo.GetEntity(userId) ?? throw new ArgumentException("用户不存在，请刷新列表。");
        if (MaintenanceInterlock.Holds(userId)) throw new InvalidOperationException("该用户仍持有维护锁，不能删除。");
        // 先保护当前会话，再检查管理员余量；删除成功后才清理目标账号的本地登录缓存。
        if (target.Id == actor.Id) throw new ArgumentException("不能删除当前登录的用户。");
        if (target.Role == (int)UserRole.Administrator && repo.GetAll().Count(m => m.Role == (int)UserRole.Administrator) <= 1)
            throw new ArgumentException("不能删除最后一个管理员。");
        if (!repo.Delete(target)) throw new InvalidOperationException("删除用户失败，请刷新列表后重试。");
        InvalidateLoginCache(target.UserName);
        return true;
    });
    /// <summary>管理员为其他用户重置密码；本人密码必须通过验证原密码的入口修改。</summary>
    public Task ResetPasswordAsync(int userId, string password, string confirmation) => Run(repo =>
    {
        var actor = Administrator(repo);
        var target = repo.GetEntity(userId) ?? throw new ArgumentException("用户不存在，请刷新列表。");
        if (target.Id == actor.Id) throw new ArgumentException("请通过“修改本人密码”输入原密码后修改。");
        if (MaintenanceInterlock.Holds(userId)) throw new InvalidOperationException("该用户仍持有维护锁，不能由他人重置密码。");
        target.Password = Password(password, confirmation);
        if (!repo.Update(target)) throw new InvalidOperationException("密码重置失败，请重试。");
        InvalidateLoginCache(target.UserName);
        return true;
    });
    /// <summary>验证原密码后修改指定账号；存在已登录会话时只能修改该会话对应的用户。</summary>
    public Task ChangePasswordAsync(string userName, string oldPassword, string newPassword, string confirmation) => Run(repo =>
    {
        var name = UserName(userName);
        var current = session.CurrentUser;
        if (current is { Id: > 0 } && !string.Equals(current.UserName, name, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("只能修改当前登录用户的密码。");
        var hash = Password(newPassword, confirmation);
        if (string.IsNullOrEmpty(oldPassword) || !repo.Login(name, MD5Helper.GetMD5_32(oldPassword), out var target))
            throw new ArgumentException("用户名或原密码错误。");
        // 名称认证之外再核对记录主键，防止会话所指的身份与认证所得账号不一致。
        if (current is { Id: > 0 } && current.Id != target.Id)
            throw new UnauthorizedAccessException("登录身份已变更，请重新登录后修改。");
        if (oldPassword == newPassword) throw new ArgumentException("新密码不能与原密码相同。");
        target.Password = hash;
        if (!repo.Update(target)) throw new InvalidOperationException("修改密码失败，请重试。");
        // 成功改密后清空当前会话中保存的旧明文密码；不把新密码写回会话。
        if (current?.Id == target.Id) current.Password = "";
        InvalidateLoginCache(target.UserName);
        return true;
    });
    // 只失效与被修改账号同名的记住登录记录；缓存故障不撤销已经完成的数据库修改。
    private void InvalidateLoginCache(string name)
    {
        try
        {
            if (cache.Get(CacheKey.User, out CurrentUser remembered) && remembered is not null &&
                string.Equals(remembered.UserName, name, StringComparison.OrdinalIgnoreCase))
            {
                // 同时关闭自动登录，防止下次启动继续尝试已删除账号或旧密码。
                cache.Delete(CacheKey.User);
                cache.Set(CacheKey.IsAutoLogin, false);
            }
        }
        catch { Logs.LogWarning("账号已更新，但登录缓存清理失败；下次登录请重新输入密码。"); }
    }
}
