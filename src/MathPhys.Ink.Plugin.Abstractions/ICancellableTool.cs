namespace MathPhys.Ink.Plugins;

/// <summary>
/// 可选标记接口（M19）：这个工具支持按 Esc 主动取消当前操作。
/// </summary>
/// <remarks>
/// 宿主在 Esc 时先问「正在绘制的工具」能不能自己取消：
/// <list type="bullet">
/// <item><see cref="TryCancel"/> 返回 <c>true</c>：工具已经取消掉当前一步（例如丢弃拖到一半的
/// 圆规半径预览），但仍保持激活状态 —— 老师可以原地继续画下一个圆；</item>
/// <item>返回 <c>false</c>：工具自己已经没有可取消的状态（比如还没开始画），宿主应切回
/// 激活它之前的那个工具，把 Esc 的「退出」语义留给宿主兜底。</item>
/// </list>
/// <para>
/// 与 <see cref="ITransientTool"/> 一样是<b>新增可选接口</b>：不改 <see cref="ITool"/> 的任何
/// 成员，老插件不认识它，行为一字不变；宿主用 <c>is</c> 判断且被判断的工具必须是实例实现。
/// </para>
/// </remarks>
public interface ICancellableTool
{
    /// <summary>
    /// 尝试取消当前绘制 / 拖动。
    /// </summary>
    /// <returns>
    /// <c>true</c> = 已取消并留在原工具；<c>false</c> = 无可取消状态，宿主切回上一工具。
    /// </returns>
    bool TryCancel();
}
