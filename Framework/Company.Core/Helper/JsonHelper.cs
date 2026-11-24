using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Company.Core.Helper
{
    /// <summary>
    /// Json文件的序列化和反序列化帮助类
    /// </summary>
    public static class JsonHelper
    {
        private static readonly ConcurrentDictionary<string, object> fileLocks = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 加载某个JSON文件并返回指定的类型实例
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public static T Load<T>(string fileName)
        {
            string path = Path.GetFullPath(fileName);
            lock (fileLocks.GetOrAdd(path, _ => new object()))
            {
                Exception primaryFailure;
                bool primaryMissing;
                try
                {
                    return (T)ReadContent(File.ReadAllText(path), typeof(T));
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    primaryFailure = exception;
                    primaryMissing = true;
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException)
                {
                    primaryFailure = exception;
                    primaryMissing = false;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"无法读取配置文件：{path}", exception);
                }

                string backupPath = path + ".bak";
                string backupContent;
                T backupValue;
                try
                {
                    backupContent = File.ReadAllText(backupPath);
                    backupValue = (T)ReadContent(backupContent, typeof(T));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    // 首次使用时两个文件都不存在，沿用原有的未配置语义。
                    if (primaryMissing && (exception is FileNotFoundException or DirectoryNotFoundException))
                        return default!;

                    throw new IOException($"配置文件及其备份无法恢复，原文件保留：{path}",
                        new AggregateException(primaryFailure, exception));
                }

                try
                {
                    if (!primaryMissing) PreserveCorruptFile(path);
                    WriteAtomically(path, backupContent, false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"配置备份有效，但恢复失败：{path}", exception);
                }

                TraceWarningSafely($"配置文件已从有效备份恢复：{path}");
                return backupValue;
            }
        }

        public static T Deserialize<T>(string content)
        {
            return JsonConvert.DeserializeObject<T>(content)!;
        }

        /// <summary>
        /// 将指定的类型实例保存为JSON文件
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="fileName"></param>
        /// <param name="indented"></param>
        public static void Save(object obj, string fileName, bool indented = false)
        {
            ArgumentNullException.ThrowIfNull(obj);
            string content = Serialize(obj, indented);
            ReadContent(content, obj.GetType());
            string path = Path.GetFullPath(fileName);
            lock (fileLocks.GetOrAdd(path, _ => new object()))
            {
                try
                {
                    bool keepPreviousVersion = false;
                    try
                    {
                        ReadContent(File.ReadAllText(path), obj.GetType());
                        keepPreviousVersion = true;
                    }
                    catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                    {
                        // 尚无正式文件，首次保存直接原子创建。
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidDataException)
                    {
                        // 缓存重建时也保留坏文件，不能用坏版本覆盖有效备份。
                        PreserveCorruptFile(path);
                    }

                    WriteAtomically(path, content, keepPreviousVersion);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"保存配置文件失败：{path}", exception);
                }
            }
        }

        private static object ReadContent(string content, Type type)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidDataException("配置文件内容为空。");

            return JsonConvert.DeserializeObject(content, type)
                ?? throw new InvalidDataException("配置文件内容为 null。");
        }

        private static void PreserveCorruptFile(string path)
        {
            string preservedPath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            File.Copy(path, preservedPath, false);
            TraceWarningSafely($"已保留损坏的配置文件：{preservedPath}");
        }

        private static void WriteAtomically(string path, string content, bool keepPreviousVersion)
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                    {
                        writer.Write(content);
                    }
                    stream.Flush(true);
                }

                if (File.Exists(path))
                    File.Replace(temporaryPath, path, keepPreviousVersion ? path + ".bak" : null);
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    TraceWarningSafely($"无法清理配置临时文件：{temporaryPath}；{exception.GetType().Name}");
                }
            }
        }

        private static void TraceWarningSafely(string message)
        {
            try { Trace.TraceWarning(message); }
            catch (Exception) { /* 诊断通道故障不能改变配置读写结果。 */ }
        }

        public static string Serialize(object obj, bool indented = false)
        {
            Formatting formatting = indented ? Formatting.Indented : Formatting.None;
            return JsonConvert.SerializeObject(obj, formatting);
        }
    }
}
