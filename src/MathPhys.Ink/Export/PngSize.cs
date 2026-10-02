namespace MathPhys.Ink.Export;

/// <summary>
/// 只读 PNG 文件头，拿回像素尺寸（<b>不解码整张图</b>）。
/// </summary>
/// <remarks>
/// <para>
/// 需要它的理由很具体：仿真面板抓回来的是一串 PNG 字节，而"把它摆到卷面上多大"必须按
/// <b>长宽比</b>算 —— 用 <c>Stretch.Fill</c> 硬拉会把圆画成椭圆，物理老师一眼就看得出。
/// </para>
/// <para>
/// 为什么不去 <c>BitmapFrame.Create</c> 解一遍：那条路要建解码器、要真的解像素，
/// 而这里只需要两个整数；而且它要求一个完整的 WPF 环境，本函数在无窗口的验收
/// harness 里也能跑。任务单一，出错面就小。
/// </para>
/// <para>
/// 判据刻意<b>宽松</b>：只要求签名对、第一个块是 <c>IHDR</c>、宽高是正数。
/// 别的一概不查（校验 CRC、走完所有块、看色彩类型）—— 我们不是解码器，
/// 而真正的判定权在后头：脏图会在 <c>BitmapImage</c> 解码那一步被拒绝并降级成占位框。
/// </para>
/// </remarks>
public static class PngSize
{
    /// <summary>PNG 的 8 字节魔数。</summary>
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>读完尺寸至少要有的字节数（签名 8 + 块头 8 + 宽高 8）。</summary>
    public const int MinLength = 24;

    /// <summary>
    /// 尝试读出像素尺寸。
    /// </summary>
    /// <returns>是合法 PNG 头且尺寸为正时 <c>true</c>；否则 <c>false</c>（<b>不抛异常</b>）。</returns>
    public static bool TryRead(byte[]? png, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (png is null || png.Length < MinLength) return false;

        for (int i = 0; i < Signature.Length; i++)
        {
            if (png[i] != Signature[i]) return false;
        }

        // 第一个块必须是 IHDR（"I" "H" "D" "R"），否则这串字节不是我们能认的 PNG
        if (png[12] != (byte)'I' || png[13] != (byte)'H'
            || png[14] != (byte)'D' || png[15] != (byte)'R')
        {
            return false;
        }

        // PNG 是网络字节序：大端
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

        if (w <= 0 || h <= 0) return false;

        width = w;
        height = h;
        return true;
    }

    /// <summary>
    /// 读尺寸，读不出来时给一个中性的兜底比例（4:3）。
    /// </summary>
    /// <remarks>
    /// 抓帧失败、或字节被截断时都会走到这里。给 4:3 而不是"报错退出"：
    /// 一张比例略有偏差的截图仍然有用（老师看得见、能重导一次），
    /// 而"因为量不出比例所以整个导出失败"是纯粹的损失。
    /// </remarks>
    public static bool TryReadOrRatio(byte[]? png, out int width, out int height)
    {
        if (TryRead(png, out width, out height)) return true;

        width = 4;
        height = 3;
        return false;
    }
}
