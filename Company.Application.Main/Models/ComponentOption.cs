/*
 * 文件职责与维护说明：
 * 设备或构件下拉框选项：Id 用于绑定和查询，Name 用于操作人员阅读。
 * 显示名称与逻辑标识不同才拼接展示，两者不能互相替代。
 */
namespace Company.Application.Main.Models;

/// <summary>来自配置的设备或构件选项；标识与显示名称分开。</summary>
public sealed record ComponentOption(string Id, string Name)
{
    public override string ToString() => Name == Id ? Id : $"{Name} · {Id}";
}
