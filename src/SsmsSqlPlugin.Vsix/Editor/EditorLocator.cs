using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Editor
{
    /// <summary>
    /// 取「当前活动的 SQL 编辑器视图」。
    ///
    /// 走 IVsTextManager.GetActiveView —— 不依赖键盘焦点，菜单点击、快捷键、
    /// 工具栏按钮三条路径拿到的都是同一个视图。
    ///
    /// 只返回 IVsTextView（TextManager.Interop 层）。
    /// 原因见 FormatExecutor 的注释：ITextView 一旦出现在包的成员签名里，
    /// SSMS 会静默不加载整个包。
    /// </summary>
    internal static class EditorLocator
    {
        /// <summary>
        /// 当前活动视图。没有活动文档（或活动文档不是文本编辑器）时返回 null。
        /// 调用方**必须**在 UI 线程。
        /// </summary>
        public static IVsTextView GetActiveView(IServiceProvider serviceProvider, bool mustBeSql)
        {
            if (serviceProvider == null) return null;

            try
            {
                var textManager = serviceProvider.GetService(typeof(SVsTextManager)) as IVsTextManager;
                if (textManager == null)
                {
                    Log.Warn("SVsTextManager 不可用");
                    return null;
                }

                IVsTextView view;
                // 第一个参数 mustHaveFocus=1 只返回有焦点的视图；
                // 菜单/工具栏点击时焦点往往已经离开编辑器，所以传 0。
                int hr = textManager.GetActiveView(0, null, out view);
                if (hr != 0 || view == null) return null;

                if (mustBeSql && !IsSqlView(view))
                {
                    Log.Info("活动视图不是 SQL 编辑器，忽略");
                    return null;
                }

                return view;
            }
            catch (Exception ex)
            {
                Log.Error("取活动视图失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 判断这个视图是不是 SQL 编辑器。
        ///
        /// 判据是文件扩展名 + 内容类型名。不用编辑器 GUID ——
        /// SSMS 的 SQL 编辑器 GUID 不是公开常量，硬编码容易随版本变。
        /// </summary>
        public static bool IsSqlView(IVsTextView view)
        {
            if (view == null) return false;

            // ① 优先看内容类型名（最可靠：.sql 文件、内联 SQL 都覆盖）
            try
            {
                string contentType = GetContentTypeName(view);
                if (!string.IsNullOrEmpty(contentType) &&
                    contentType.IndexOf("SQL", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读内容类型失败: " + ex.Message);
            }

            // ② 退一步看 buffer 的文件扩展名
            try
            {
                string path = GetDocumentPath(view);
                if (!string.IsNullOrEmpty(path) &&
                    path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读文档路径失败: " + ex.Message);
            }

            return false;
        }

        /// <summary>
        /// 通过 IVsEditorAdaptersFactoryService 拿到该视图的 ITextBuffer，
        /// 再读 ContentType.TypeName。全程反射式访问，不把 ITextView 写进签名。
        /// </summary>
        private static string GetContentTypeName(IVsTextView view)
        {
            var provider = ServiceProviderHolder.Provider;
            if (provider == null) return null;

            var componentModel = provider.GetService(typeof(SComponentModel)) as IComponentModel;
            if (componentModel == null) return null;

            var adapter = componentModel.GetService<IVsEditorAdaptersFactoryService>();
            if (adapter == null) return null;

            // 注意：这里拿到的是 object（运行时是 IWpfTextView），
            // 全程用反射访问，静态类型里不出现 ITextView。
            object textView = adapter.GetWpfTextView(view);
            if (textView == null) return null;

            object buffer = textView.GetType()
                .GetProperty("TextBuffer")
                ?.GetValue(textView, null);
            if (buffer == null) return null;

            object contentType = buffer.GetType()
                .GetProperty("ContentType")
                ?.GetValue(buffer, null);
            if (contentType == null) return null;

            object typeName = contentType.GetType()
                .GetProperty("TypeName")
                ?.GetValue(contentType, null);

            return typeName as string;
        }

        private static string GetDocumentPath(IVsTextView view)
        {
            IVsTextLines buffer;
            if (view.GetBuffer(out buffer) != 0 || buffer == null) return null;

            IVsUserData userData = buffer as IVsUserData;
            if (userData == null) return null;

            // VSUSERDATA_FILEPATH = {2c8db274-...}
            var guidFile = new Guid("2C8DB274-D9D4-4B48-9E5B-8A2D5D2F3F72");
            object value;
            if (userData.GetData(ref guidFile, out value) != 0) return null;

            return value as string;
        }
    }

    /// <summary>
    /// 全局 ServiceProvider 持有者。
    ///
    /// 包初始化时注入一次，之后静态工具类可以直接取服务，
    /// 不必把 IServiceProvider 一路往下传。
    /// </summary>
    internal static class ServiceProviderHolder
    {
        public static IServiceProvider Provider { get; set; }
    }
}
