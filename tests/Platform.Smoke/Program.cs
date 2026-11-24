using System.ComponentModel;
using System.Text.Json;
using Company.Core.Cache;
using Company.Core.Helper;
using Company.Database.Accounts;
using Company.Platform.Services.Accounts;
using Company.Platform.Services.Authorization;
using Company.Platform.Services.Identity;
using Company.Platform.Services.Login;
using Company.Platform.Shared.Login;
using Company.Platform.Shared.Model;
using Newtonsoft.Json.Linq;

var results=new List<(string Name,bool Passed)>();
void Check(string name,bool value){results.Add((name,value));if(!value)throw new Exception(name);}
async Task Run(string name,Func<Task> action){try{await action();results.Add((name,true));}catch(Exception e){results.Add((name,false));Console.Error.WriteLine(name+": "+e.GetType().Name+" "+e.Message);}}
async Task Throws<T>(string name,Func<Task> action)where T:Exception{try{await action();Check(name,false);}catch(T){Check(name,true);}}
await Run("portable.roles-and-default-denial",()=>
{
    foreach(int role in new[]{0,1,2,3,4,99})
    {
        var user=new CurrentUser{Id=1,UserName="synthetic",Role=role};
        Check("role."+role+".login",RoleAccessRules.IsLoggedIn(user)==(role is >=1 and <=4));
        Check("role."+role+".admin",RoleAccessRules.IsAdministrator(user)==(role==1));
        Check("role."+role+".configure",RoleAccessRules.CanConfigureEquipment(user)==(role is 1 or 3 or 4));
        Check("role."+role+".unknown",!RoleAccessRules.IsGranted(user,"unknown"));
    }
    Check("empty-identity-denied",!RoleAccessRules.IsLoggedIn(new(){Id=1,Role=1})&&!RoleAccessRules.IsLoggedIn(new(){UserName="test",Role=1}));
    return Task.CompletedTask;
});
await Run("portable.session-events-and-custom-application-policy",()=>
{
    var session=new Session();var policy=new CustomPolicy();using var authorization=new UserAuthorizationService(session,policy);
    var notifications=new List<string>();session.PropertyChanging+=(_,e)=>notifications.Add("before:"+e.PropertyName);session.PropertyChanged+=(_,e)=>notifications.Add("after:"+e.PropertyName);
    var identity=new CurrentUser{Id=1,UserName="test",Role=3};session.CurrentUser=identity;
    Check("session.standard-event-order",notifications.SequenceEqual(new[]{"before:CurrentUser","after:CurrentUser"}));
    session.CurrentUser=identity;Check("session.same-reference-no-event",notifications.Count==2);
    Check("custom.permission-denied-before-ready",authorization.IsLoggedIn&&!authorization.IsGranted("Custom.Scan"));
    var version=authorization.Version;policy.SetReady(true);
    Check("custom.ready-grants-and-invalidates",authorization.IsGranted("Custom.Scan")&&authorization.Version>version);
    Check("custom.unknown-denied",!authorization.IsGranted("Custom.Unknown"));
    int changed=0;authorization.Changed+=(_,_)=>changed++;authorization.Dispose();
    policy.SetReady(false);session.CurrentUser=new();Check("disposed-service-unsubscribed",changed==0);
    return Task.CompletedTask;
});
await Run("portable.accounts-and-application-mutation-policy",async()=>
{
    var repo=new Repository();repo.Insert(new(){UserName="admin-test",PasswordHash=MD5Helper.GetMD5_32("admin-pass"),Role=1,CreatedAt=DateTime.Now});
    var session=new Session{CurrentUser=new(){Id=1,UserName="admin-test",Role=1}};var cache=new Cache();var guard=new MutationPolicy();
    var service=new UserAccountService(()=>repo,session,cache,guard);
    await service.CreateUserAsync("engineer","test-pass","test-pass",UserRole.EquipmentEngineer);
    Check("account.legacy-hash-authentication",await service.AuthenticateAsync(" engineer ","test-pass") is{Role:3});
    guard.Blocked=true;
    await Throws<InvalidOperationException>("account.app-policy-delete",()=>service.DeleteUserAsync(2));
    await Throws<InvalidOperationException>("account.app-policy-reset",()=>service.ResetPasswordAsync(2,"reset-pass","reset-pass"));
    Check("account.denial-before-storage-write",repo.FindById(2) is not null&&await service.AuthenticateAsync("engineer","test-pass") is not null);
    guard.Blocked=false;await service.ResetPasswordAsync(2,"reset-pass","reset-pass");
    session.CurrentUser=new(){Id=2,UserName="engineer",Role=3,Password="transient-only"};
    await Throws<UnauthorizedAccessException>("account.engineer-no-admin",async()=>_=await service.GetUsersAsync());
    await Throws<ArgumentException>("account.old-password-required",()=>service.ChangePasswordAsync("engineer","bad-pass","new-pass","new-pass"));
    cache.Set(CacheKey.User,new{UserName="engineer",ProtectionVersion=1,ProtectedPassword="test-cipher"});cache.Set(CacheKey.IsAutoLogin,true);
    await service.ChangePasswordAsync("engineer","reset-pass","new-pass","new-pass");
    Check("account.own-password-changed",await service.AuthenticateAsync("engineer","new-pass") is not null&&session.CurrentUser.Password=="");
    Check("account.protected-cache-invalidated",!cache.Get<JToken>(CacheKey.User,out _)&&cache.Get<bool>(CacheKey.IsAutoLogin,out var auto)&&!auto);
    session.CurrentUser=new(){Id=1,UserName="admin-test",Role=1};repo.ChangeRole(1,2);
    await Throws<UnauthorizedAccessException>("account.database-role-revalidated",async()=>_=await service.GetUsersAsync());
    repo.ChangeRole(1,1);await service.DeleteUserAsync(2);Check("account.deleted",await service.AuthenticateAsync("engineer","new-pass") is null);
});
await Run("portable.preferences-with-pluggable-protector",()=>
{
    var cache=new Cache();var protector=new Protector();var store=new RememberedLoginStore(cache,protector,new("synthetic-app|","default"));
    cache.Set(CacheKey.User,new{UserName="synthetic",Password="legacy-test-password"});cache.Set(CacheKey.IsRemember,true);cache.Set(CacheKey.IsAutoLogin,true);cache.Writes.Clear();
    var migrated=store.Load();Check("preferences.legacy-migrated",migrated.Remember&&migrated.AutoLogin&&migrated.Password=="legacy-test-password");
    Check("preferences.no-plaintext-write",cache.Writes.All(x=>!x.Contains("legacy-test-password")));
    Check("preferences.no-credentials-in-display",!migrated.ToString().Contains("legacy-test-password")&&!migrated.ToString().Contains("synthetic"));
    Check("preferences.protected-reload",store.Load().Password=="legacy-test-password");
    var differentPurpose=new RememberedLoginStore(cache,protector,new("other-app|","default"));
    Check("preferences.wrong-purpose-no-auto",!differentPurpose.Load().AutoLogin);
    cache.Set(CacheKey.User,new{UserName="synthetic",ProtectionVersion=2,ProtectedPassword="broken",Password="must-not-fallback"});cache.Set(CacheKey.IsRemember,true);cache.Set(CacheKey.IsAutoLogin,true);
    var invalid=store.Load();Check("preferences.unknown-version-no-plaintext-fallback",!invalid.Remember&&!invalid.AutoLogin&&invalid.Password=="");
    store.Save(new(){UserName="synthetic",Password="new-synthetic-password"},true,true);
    Check("preferences.only-protected-shape",cache.Get<JObject>(CacheKey.User,out var shape)&&shape.Property("Password")==null&&shape.Property("Role")==null&&shape.Property("ProtectedPassword")!=null);
    store.Save(new(),false,false);Check("preferences.clear-disables-auto",!cache.Get<JToken>(CacheKey.User,out _)&&cache.Get<bool>(CacheKey.IsAutoLogin,out var auto)&&!auto);
    return Task.CompletedTask;
});
await Run("portable.no-desktop-or-device-dependency",()=>
{
    var names=AppDomain.CurrentDomain.GetAssemblies().Select(x=>x.GetName().Name??"").ToArray();
    Check("platform.no-plc-hmi",!names.Any(n=>n.StartsWith("Company.PlcHmi")||n.StartsWith("Company.Hardware")));
    Check("platform.no-wpf-or-reactive",!names.Any(n=>n.StartsWith("Presentation")||n is "Company.Desktop" or "Prism.Wpf" or "ReactiveUI"));
    Check("platform.no-orm",!names.Any(n=>n.StartsWith("SqlSugar")));
    return Task.CompletedTask;
});
var report=new{total=results.Count,passed=results.Count(x=>x.Passed),failed=results.Where(x=>!x.Passed).Select(x=>x.Name).ToArray(),realDatabaseConnected=false,realDeviceConnected=false};
if(args.Length>0)File.WriteAllText(args[0],JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Platform checks: {report.passed}/{report.total}; failed={report.failed.Length}; no application/device/ORM dependency.");return report.failed.Length==0?0:1;

public sealed class CustomPolicy:IUserAuthorizationPolicy
{
    private bool _ready;public event EventHandler? Changed;
    public void SetReady(bool value){_ready=value;Changed?.Invoke(this,EventArgs.Empty);}
    public bool IsGranted(CurrentUser? user,string permission)=>permission=="Custom.Scan"&&_ready&&RoleAccessRules.CanConfigureEquipment(user);
    public void OnIdentityChanged() { }
}
public sealed class MutationPolicy:IAccountMutationPolicy
{
    public bool Blocked;public void CheckDelete(UserAccountInfo target){if(Blocked)throw new InvalidOperationException("synthetic lock");}
    public void CheckResetPassword(UserAccountInfo target){if(Blocked)throw new InvalidOperationException("synthetic lock");}
}
public sealed class Cache:ICacheManager
{
    private readonly Dictionary<string,JToken> _values=new();public List<string> Writes=new();
    public bool Get<T>(ValueType key,out T value){if(_values.TryGetValue(key.ToString()!,out var token)){value=token.ToObject<T>()!;return true;}value=default!;return false;}
    public void Set<T>(ValueType key,T value){var token=JToken.FromObject(value!);_values[key.ToString()!]=token;Writes.Add(token.ToString());}
    public void Delete(ValueType key)=>_values.Remove(key.ToString()!);
}
public sealed class Protector:ILoginSecretProtector
{
    // 只用于纯服务测试；生产注册使用 WindowsLoginSecretProtector，不能把本替身用于真实密码。
    private readonly Dictionary<string,(string User,string Purpose,string Password)> _values=new();
    public string Protect(string userName,string password,string purpose){var key=Guid.NewGuid().ToString("N");_values[key]=(userName,purpose,password);return key;}
    public string Unprotect(string userName,string ciphertext,string purpose)=>_values.TryGetValue(ciphertext,out var v)&&v.User==userName&&v.Purpose==purpose?v.Password:throw new System.Security.Cryptography.CryptographicException();
}
public sealed class Repository:IUserAccountRepository
{
    private readonly Dictionary<int,UserAccount> _users=new();private int _next;
    private static UserAccount Copy(UserAccount u)=>new(){Id=u.Id,UserName=u.UserName,PasswordHash=u.PasswordHash,Role=u.Role,CreatedAt=u.CreatedAt};
    public UserAccount? FindById(int id)=>_users.TryGetValue(id,out var u)?Copy(u):null;
    public IReadOnlyList<UserAccount> List()=>_users.Values.Select(Copy).ToArray();
    public bool TryAuthenticate(string name,string hash,[System.Diagnostics.CodeAnalysis.NotNullWhen(true)]out UserAccount? account){account=_users.Values.FirstOrDefault(u=>u.UserName==name&&u.PasswordHash==hash);if(account!=null)account=Copy(account);return account!=null;}
    public bool Insert(UserAccount u){u.Id=++_next;_users.Add(u.Id,Copy(u));return true;}
    public bool Update(UserAccount u){if(!_users.ContainsKey(u.Id))return false;_users[u.Id]=Copy(u);return true;}
    public bool Delete(int id)=>_users.Remove(id);public void ChangeRole(int id,int role)=>_users[id].Role=role;
}
