using Company.Core.Enums;
using Company.Core.Ioc;
using System;
using System.Linq;
using System.Windows;

namespace Company.Core.Language
{
    [ExposedService(Lifetime.Singleton, typeof(ILanguageManager))]
    public class LanguageManager : ILanguageManager
    {
        private ResourceDictionary? resource;

        public LanguageManager()
        {
            Set(LanguageType.CN);//设置默认语言为简体中文
        }

        public string this[string key]
        {
            get
            {
                ArgumentNullException.ThrowIfNull(key);
                var application = GetApplication();
                return application.Dispatcher.Invoke(() =>
                    resource != null && resource.Contains(key)
                        ? resource[key]?.ToString() ?? key
                        : key);
            }
        }

        public LanguageType Current { get; private set; }

        public void Set(LanguageType language)
        {
            if (!Enum.IsDefined(language))
                throw new ArgumentOutOfRangeException(nameof(language));

            var application = GetApplication();
            application.Dispatcher.Invoke(() =>
            {
                var dictionaries = application.Resources.MergedDictionaries;
                var existing = resource != null && dictionaries.Contains(resource)
                    ? resource
                    : dictionaries.SingleOrDefault(IsLanguageDictionary);
                if (existing?.Source == null)
                    throw new InvalidOperationException("未找到语言资源字典，语言资源必须通过 Source 加载。");

                var source = GetSourcePath(existing)!;
                var target = source.Substring(0, source.LastIndexOf('/') + 1) + language + ".xaml";
                // 完整加载成功后才替换，保留其他资源及原有顺序。
                var replacement = new ResourceDictionary
                {
                    Source = new Uri(target, UriKind.RelativeOrAbsolute)
                };
                var index = dictionaries.IndexOf(existing);
                dictionaries[index] = replacement;
                resource = replacement;
                Current = language;
            });
        }

        private static bool IsLanguageDictionary(ResourceDictionary dictionary)
        {
            var source = GetSourcePath(dictionary);
            if (source == null) return false;
            var slash = source.LastIndexOf('/');
            if (slash < 0 || !source.Substring(0, slash).EndsWith("/Language", StringComparison.OrdinalIgnoreCase))
                return false;
            return Enum.GetNames<LanguageType>().Any(name =>
                string.Equals(source.Substring(slash + 1), name + ".xaml", StringComparison.OrdinalIgnoreCase));
        }

        private static string? GetSourcePath(ResourceDictionary dictionary)
        {
            var source = dictionary.Source;
            return source?.IsAbsoluteUri == true
                ? source.AbsoluteUri
                : source?.OriginalString.Replace('\\', '/');
        }

        private static Application GetApplication()
        {
            var application = Application.Current
                ?? throw new InvalidOperationException("语言资源需要已初始化的 WPF Application。");
            if (application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
                throw new InvalidOperationException("应用程序正在退出，无法访问语言资源。");
            return application;
        }
    }
}
