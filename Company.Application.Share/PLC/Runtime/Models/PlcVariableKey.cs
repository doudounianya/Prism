using Company.Application.Share.PLC.Configuration;
namespace Company.Application.Share.PLC.Runtime.Models;

/// <summary>协议、设备和逻辑标识组成的变量唯一键；避免不同设备或协议下相同逻辑名称发生缓存碰撞。</summary>
public sealed record PlcVariableKey(PlcProtocol Protocol, string DeviceId, string LogicalId);
