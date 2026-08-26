using System.Runtime.InteropServices;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 在运行时绘制托盘图标，避免发布包依赖外部 ico 文件。
/// </summary>
public static class IconFactory
{
    /// <summary>
    /// 创建带百分比环和美元符号的 Windows 托盘图标。
    /// </summary>
    /// <param name="usedPercent">当前额度已用百分比；未知时传 null。</param>
    /// <returns>调用方负责释放的独立 Icon 对象。</returns>
    public static Icon Create(decimal? usedPercent)
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var background = new SolidBrush(Color.FromArgb(35, 43, 58));
        graphics.FillEllipse(background, 1, 1, 30, 30);

        using var basePen = new Pen(Color.FromArgb(85, 100, 125), 4);
        graphics.DrawArc(basePen, 4, 4, 24, 24, -90, 360);
        if (usedPercent is not null)
        {
            var clamped = Math.Clamp(usedPercent.Value, 0, 100);
            var color = clamped >= 90 ? Color.OrangeRed : clamped >= 70 ? Color.Gold : Color.DeepSkyBlue;
            using var progressPen = new Pen(color, 4) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            graphics.DrawArc(progressPen, 4, 4, 24, 24, -90, (float)(360m * clamped / 100m));
        }

        using var font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        var text = "$";
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, textBrush, (32 - size.Width) / 2, (32 - size.Height) / 2);

        var handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    /// <summary>
    /// 释放 Bitmap.GetHicon 创建的非托管图标句柄。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
