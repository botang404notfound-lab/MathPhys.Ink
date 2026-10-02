using System.IO;
using System.Security.Cryptography;
using MathPhys.Ink.Infrastructure;

namespace MathPhys.Ink.Ink;

/// <summary>
/// PDF 的<b>内容指纹</b>：用来回答"这份 PDF 和批注记录的那一份，是不是同一个文件"。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不直接用文件长度 + 修改时间：老师把试卷拷来拷去（U 盘、一体机、微信传一遍），
/// 修改时间几乎必然改变，而内容其实一模一样 —— 那样每次打开都会误判成"对不上"。
/// 内容哈希则不受拷贝、改名、移动的影响。
/// </para>
/// <para>
/// 为什么不整份做 SHA256：一份几十上百 MB 的扫描版试卷，全量哈希会让打开文档卡顿。
/// 这里取 <c>SHA256(长度 ‖ 前 1 MiB ‖ 后 1 MiB)</c>：
/// 小于 2 MiB 的文件等价于整份哈希；更大的文件也只需读 2 MiB，对试卷这种
/// "内容分布均匀、首尾都有关键结构"的文档，区分度完全够用。
/// </para>
/// <para>
/// <b>这是"同一份吗"的判据，不是密码学用途</b> —— 不用于防篡改，只用于避免批注错配。
/// </para>
/// </remarks>
public static class DocumentFingerprint
{
    /// <summary>首尾各取多少字节参与哈希。</summary>
    private const int Window = 1 << 20;

    /// <summary>指纹取哈希前多少字节（16 字节 = 32 个十六进制字符，足够区分且便于阅读）。</summary>
    private const int FingerprintBytes = 16;

    /// <summary>
    /// 计算内容指纹。读不到文件时返回空串（调用方据此判定"无法校验"）。
    /// </summary>
    public static string Compute(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return string.Empty;

        try
        {
            // FileShare.ReadWrite：即使 PDF 正被阅读器打开，也能算指纹
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            long length = stream.Length;

            // 文件不足两个窗口时整份读，避免首尾重叠导致同一段被喂两次
            bool wholeFile = length <= 2L * Window;
            int headLength = (int)Math.Min(Window, length);
            int tailLength = wholeFile ? 0 : (int)Math.Min(Window, length);

            var buffer = new byte[sizeof(long) + headLength + tailLength];

            // 把长度混进去：内容相同但长度不同的文件不该得到同一个指纹
            BitConverter.TryWriteBytes(buffer.AsSpan(0, sizeof(long)), length);

            stream.Position = 0;
            stream.ReadExactly(buffer.AsSpan(sizeof(long), headLength));

            if (tailLength > 0)
            {
                stream.Position = length - tailLength;
                stream.ReadExactly(buffer.AsSpan(sizeof(long) + headLength, tailLength));
            }

            byte[] hash = SHA256.HashData(buffer);
            return Convert.ToHexString(hash, 0, FingerprintBytes).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"无法计算文档指纹（{filePath}）：{ex.Message}");
            return string.Empty;
        }
    }
}
