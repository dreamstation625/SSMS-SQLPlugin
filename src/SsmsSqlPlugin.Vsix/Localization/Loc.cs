using System;
using System.Globalization;
using System.Resources;
using SsmsSqlPlugin.Editor;

namespace SsmsSqlPlugin.Localization
{
    /// <summary>
    /// UI 文案的本地化入口。
    ///
    /// 资源：Resources/Strings.resx（中文，默认）+ Resources/Strings.en.resx（英文卫星程序集）。
    /// key 直接用英文文案；缺资源时返回 key 本身 —— 宁可在界面上看到英文，
    /// 也不能因为少一条资源把窗口炸掉。
    ///
    /// 语言偏好来自 FormatOptionsStore.UiLanguage（auto / zh-CN / en），
    /// 包初始化最早期调用 Loc.Configure(...)，因此切换语言后重启 SSMS 才生效。
    /// </summary>
    internal static class Loc
    {
        private const string ResourceBaseName = "SsmsSqlPlugin.Resources.Strings";

        private static readonly object Gate = new object();
        private static readonly ResourceManager Manager =
            new ResourceManager(ResourceBaseName, typeof(Loc).Assembly);

        private static volatile string _preference;
        private static volatile CultureInfo _culture;

        /// <summary>当前语言偏好：auto / zh-CN / en。</summary>
        public static string Preference => _preference ?? "auto";

        public static void Configure(string preference)
        {
            lock (Gate)
            {
                string normalized = Normalize(preference);
                _preference = normalized;
                _culture = Resolve(normalized);
            }
        }

        public static string T(string key)
        {
            string value;
            return TryT(key, out value) ? value : (key ?? string.Empty);
        }

        /// <summary>取当前语言文案；资源不存在时返回 false（调用方决定回退）。</summary>
        public static bool TryT(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) return false;

            try
            {
                value = Manager.GetString(key, CurrentCulture);
                return !string.IsNullOrEmpty(value);
            }
            catch
            {
                // 资源缺失/损坏时交给调用方回退，保证 UI 仍能显示
                return false;
            }
        }

        /// <summary>
        /// 中性（中文默认）资源值。用于识别引擎层产出的固定数据标签，
        /// 代码里不出现中文字面量，也不受当前 UI 语言影响。
        /// </summary>
        public static string Neutral(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;

            try { return Manager.GetString(key, CultureInfo.GetCultureInfo("zh-CN")) ?? key; }
            catch { return key; }
        }

        public static string F(string key, params object[] args)
        {
            string format = T(key);
            if (args == null || args.Length == 0) return format;

            try { return string.Format(CultureInfo.CurrentCulture, format, args); }
            catch (FormatException) { return format; }
        }

        private static CultureInfo CurrentCulture
        {
            get
            {
                var culture = _culture;
                if (culture != null) return culture;

                // 兜底：包初始化没走到 Configure 时，从设置里取一次
                Configure(FormatOptionsStore.Current.UiLanguage);
                return _culture ?? CultureInfo.CurrentUICulture;
            }
        }

        private static string Normalize(string preference)
        {
            if (string.IsNullOrEmpty(preference)) return "auto";

            string value = preference.Trim();
            if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "auto";
            if (value.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            if (value.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("en-", StringComparison.OrdinalIgnoreCase)) return "en";
            return "auto";
        }

        private static CultureInfo Resolve(string preference)
        {
            if (preference == "zh-CN") return new CultureInfo("zh-CN");
            if (preference == "en") return new CultureInfo("en");

            // auto：中文系统走中文，其它一律英文
            var system = CultureInfo.CurrentUICulture;
            if (system != null && system.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return new CultureInfo("zh-CN");
            return new CultureInfo("en");
        }
    }
}
