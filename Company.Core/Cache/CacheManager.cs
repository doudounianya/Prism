using Company.Core.Config;
using Company.Core.Extension;
using Company.Core.Helper;
using Company.Core.Ioc;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Company.Core.Cache
{
    /// <summary>
    /// 提供缓存服务的管理器
    /// </summary>
    [ExposedService(Lifetime.Singleton, typeof(ICacheManager))]
    class CacheManager : ICacheManager
    {
        private readonly IConfigManager configManager;
        private Dictionary<string,string> cacheNames;
        private readonly object cacheLock = new object();
        public CacheManager(IConfigManager configManager)
        {
            this.configManager = configManager;
            try
            {
                cacheNames = configManager.Read<Dictionary<string, string>>(ConfigKey.CacheConfig) ?? new Dictionary<string, string>();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // 只有非关键缓存可以降级为空；关键配置的读取异常仍由配置服务上抛。
                cacheNames = new Dictionary<string, string>();
                ReportCacheFailure($"缓存文件无法加载，本次按未命中处理。{exception}");
            }
        }
        

        /// <summary>
        /// 获取本地缓存内容
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public bool Get<T>(ValueType key, out T value)
        {
            value = default!;
            string? obj;
            lock (cacheLock)
            {
                if (!cacheNames.TryGetValue(key.GetFullName(), out obj) || string.IsNullOrWhiteSpace(obj))
                    return false;
            }

            try
            {
                value = JsonHelper.Deserialize<T>(obj);
                return value is not null;
            }
            catch (JsonException exception)
            {
                value = default!;
                ReportCacheFailure($"缓存项 {key.GetFullName()} 解析失败，按未命中处理。异常类型：{exception.GetType().Name}");
                return false;
            }
        }

        public void Set<T>(ValueType key, T value)
        {
            Assert.NotNull(key);
            Assert.NotNull(value);

            string content = JsonHelper.Serialize(value);

            lock (cacheLock)
            {
                var updated = new Dictionary<string, string>(cacheNames)
                {
                    [key.GetFullName()] = content
                };
                configManager.Write(ConfigKey.CacheConfig, updated);
                cacheNames = updated;

            }
        }

        /// <summary>
        /// 删除缓存内容
        /// </summary>
        /// <param name="key"></param>
        public void Delete(ValueType key)
        {
            lock (cacheLock)
            {
                if (cacheNames.ContainsKey(key.GetFullName()))
                {
                    var updated = new Dictionary<string, string>(cacheNames);
                    updated.Remove(key.GetFullName());
                    configManager.Write(ConfigKey.CacheConfig, updated);
                    cacheNames = updated;
                }
            }
        }

        private static void ReportCacheFailure(string message)
        {
            try { Trace.TraceWarning(message); }
            catch (Exception) { /* 缓存诊断失败不阻断登录。 */ }
        }
    }
}
