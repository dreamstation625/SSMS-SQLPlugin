using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using stdole;

namespace SsmsSqlPlugin.Menus
{
    /// <summary>
    /// 菜单左侧图标：用 Segoe MDL2 Assets 画 16x16，再转成 CommandBars 需要的 IPictureDisp。
    /// 若宿主不支持 Picture，调用方会退回纯文字，不影响点击。
    /// </summary>
    internal static class MenuIcons
    {
        private const int HiSize = 64;
        private static readonly System.Drawing.Font IconFont;

        static MenuIcons()
        {
            try
            {
                var font = new System.Drawing.Font("Segoe MDL2 Assets", 52f,
                    System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
                IconFont = font.Name == "Segoe MDL2 Assets" ? font : null;
            }
            catch { IconFont = null; }
        }

        public static IPictureDisp Get(string tag)
        {
            string glyph = GlyphFor(tag);
            if (string.IsNullOrEmpty(glyph) || IconFont == null) return null;

            try
            {
                using (var bitmap = DrawGlyph(glyph))
                {
                    var method = typeof(AxHost).GetMethod(
                        "GetIPictureDispFromPicture",
                        BindingFlags.NonPublic | BindingFlags.Static);

                    return method?.Invoke(null, new object[] { bitmap }) as IPictureDisp;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string GlyphFor(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;

            switch (tag)
            {
                case "SsmsSqlPlugin:Format": return "\uE70F";             // 编辑/铅笔
                case "SsmsSqlPlugin:History": return "\uE81C";            // 历史
                case "SsmsSqlPlugin:Settings": return "\uE713";           // 设置
                case "SsmsSqlPlugin:RefreshCompletion": return "\uE72C";  // 刷新
                case "SsmsSqlPlugin:Log": return "\uE8A5";                // 文档
                case "SsmsSqlPlugin:CheckUpdates": return "\uE895";       // 同步/更新
                case "SsmsSqlPlugin:About": return "\uE946";              // 信息
                default: return null;
            }
        }

        private static Bitmap DrawGlyph(string glyph)
        {
            using (var large = new Bitmap(HiSize, HiSize, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(large))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                using (var brush = new SolidBrush(Color.FromArgb(64, 72, 86)))
                using (var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoClip,
                })
                {
                    g.DrawString(glyph, IconFont, brush,
                        new RectangleF(0, 0, HiSize, HiSize), format);
                }

                var small = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
                using (var target = Graphics.FromImage(small))
                {
                    target.Clear(Color.Transparent);
                    target.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    target.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    target.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    target.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    target.DrawImage(large, new Rectangle(0, 0, 16, 16));
                }
                return small;
            }
        }
    }
}
