namespace MathPhys.Ink.Plugin.NativeSim;

/// <summary>
/// 本插件的工具 Id。
/// </summary>
/// <remarks>
/// ★ <b>这个类里只许放工具 Id 这一个字符串常量。</b>验收 harness 是按
/// 「<c>static class *ToolIds</c> 的类体里所有 <c>const string</c>」来抽取"声明了哪些工具"的，
/// 多放一个别的常量（键名、前缀…）就会被当成"一个没登记的工具"而报错。
/// </remarks>
public static class NativeSimToolIds
{
    /// <summary>物理仿真（单摆 / 弹簧振子 / 斜面）。</summary>
    public const string Id = "phys-sim";
}
