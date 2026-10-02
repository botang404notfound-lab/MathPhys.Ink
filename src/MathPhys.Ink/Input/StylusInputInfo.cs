using System.Windows.Input;

namespace MathPhys.Ink.Input;

/// <summary>
/// 一次笔输入事件里"我们对设备关心的那点事实"（纯数据）。
/// </summary>
/// <remarks>
/// 之所以不让上层直接去读 <c>StylusDevice</c>，是为了给<b>笔尾反转探测</b>留一个单点替换位置：
/// 全工程只有 <see cref="From"/> 这一处会把 WPF 的设备对象翻译成本结构。
/// <para>
/// 一体机上如果日志显示 <c>inverted</c> 恒为 false（部分一体机笔驱动不向 WPF 报反转标志），
/// 只需换掉这一处的取值来源（改为挂 <c>WM_POINTERUPDATE</c> 读 <c>PEN_FLAG_ERASER</c>），
/// 策略层与 UI 层零改动。
/// </para>
/// <para>
/// 刻意<b>不</b>引接口：只有一个实现、且"驱动到底报不报"这件事尚未被实测证实时，
/// 接口只是一层没有收益的间接。
/// </para>
/// </remarks>
public readonly record struct StylusInputInfo(TabletDeviceType DeviceType, bool Inverted, string Name)
{
    /// <summary>从 WPF 设备对象取值。⚠️ M4.1 的笔尾反转兜底<b>只改这里</b>。</summary>
    public static StylusInputInfo From(StylusDevice device)
    {
        var tablet = device.TabletDevice;
        return new StylusInputInfo(tablet.Type, device.Inverted, tablet.Name);
    }
}
