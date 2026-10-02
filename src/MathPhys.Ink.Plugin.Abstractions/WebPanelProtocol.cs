using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 通用 Web 面板的<b>信封编解码 + 类型常量</b>（M22）。
/// </summary>
/// <remarks>
/// 与 <see cref="GeoGebraProtocol"/> 并存，<b>不是它的替代</b>：
/// <list type="bullet">
/// <item><see cref="GeoGebraProtocol"/> 的常量与签名<b>一字未动</b> —— 它是
/// <c>Assets/geogebra/*.js</c> 与验收 harness 共同依赖的既有契约，动它风险远大于收益；</item>
/// <item>本类只承载"任何 Web 仿真页面都会用到"的生命周期消息，让控制器不必认识具体应用。</item>
/// </list>
/// <para>
/// 编解码放在<b>契约层</b>而不是塞进 WebView2 后端，是为了让验收 harness
/// 能在没有 WebView2 的环境里断言"两边说的一样"。
/// </para>
/// </remarks>
public static class WebPanelProtocol
{
    // ---------------------------------------------------------------- 宿主 → JS

    /// <summary>
    /// 宿主在"重新挂载"（关闭再打开 / 形态切换）后向页面打招呼；
    /// 页面应重发一条 <see cref="Ready"/>（页面本体一直在跑，但宿主侧的就绪闸门复位了）。
    /// </summary>
    public const string Hello = "hello";

    /// <summary>要求页面导出当前画面（页面可回 <see cref="ExportPng"/>；宿主默认走 WebView2 侧抓帧）。</summary>
    public const string RequestExportPng = "requestExportPng";

    // ---------------------------------------------------------------- JS → 宿主

    /// <summary>页面初始化完成（★ 就绪闸门的放行信号）。</summary>
    public const string Ready = "ready";

    /// <summary>画面变了（可选上报，宿主侧按 ≥200ms 合并）。</summary>
    public const string Changed = "changed";

    /// <summary>面板里出错了。★ 所有 JS 异常都必须回这条，不许静默失败。</summary>
    public const string Error = "error";

    /// <summary>
    /// 应用没能载入（页面自己报的，payload 带中文 <c>reason</c> 与可选 <c>detail</c>）。
    /// </summary>
    /// <remarks>
    /// ★ 收到它必须<b>倒掉待发队列</b>并停止继续排队 ——
    /// 否则加载失败时宿主的命令会一直攒着等一个永远不来的 <see cref="Ready"/>，
    /// 用户看到的就是本项目最怕的「点了没反应」。
    /// </remarks>
    public const string LoadFailed = "loadFailed";

    /// <summary>面板自检结果（仅发布冒烟用）。</summary>
    public const string SelfTest = "selfTest";

    /// <summary>面板内部日志（仅 <c>?selftest=1</c> 时回传，避免污染正式日志）。</summary>
    public const string Diagnostic = "diagnostic";

    /// <summary>导出结果（payload: <c>{ dataUrl }</c>）。</summary>
    public const string ExportPng = "exportPng";

    /// <summary>把一条消息编码成 JSON 文本。</summary>
    /// <param name="type">消息类型。空或空白会抛 <see cref="ArgumentException"/>（写错了要立刻知道）。</param>
    /// <param name="payload">
    /// payload。可以是任意可序列化对象、<see cref="JsonNode"/>（原样嵌入），或 <c>null</c>。
    /// <b>字符串按"字符串值"处理</b>，要传原始 JSON 请传 <see cref="JsonNode"/>。
    /// </param>
    public static string Encode(string type, object? payload = null)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("消息 type 不能为空。", nameof(type));
        }

        var root = new JsonObject { ["type"] = type };

        if (payload is not null)
        {
            root["payload"] = payload switch
            {
                // JsonValue 也是 JsonNode 的子类，会一并走到这一支 —— 这正是想要的：
                // 调用方传 JsonNode/JsonValue 表示"这段 JSON 原样嵌入，不要再序列化一次"。
                JsonNode node => node,
                _ => JsonSerializer.SerializeToNode(payload),
            };
        }

        return root.ToJsonString();
    }

    /// <summary>
    /// 解析一条来自面板的消息。
    /// </summary>
    /// <returns>成功为 <c>true</c>；失败时 <paramref name="error"/> 给出中文原因。</returns>
    public static bool TryDecode(string? json, out WebPanelMessage message, out string? error)
    {
        message = new WebPanelMessage(string.Empty, null);

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "消息为空。";
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"消息不是合法 JSON：{ex.Message}";
            return false;
        }

        if (root is not JsonObject obj)
        {
            error = "消息不是 JSON 对象（缺少 type 字段的外层）。";
            return false;
        }

        string? type;
        try
        {
            type = obj["type"]?.GetValue<string>();
        }
        catch (Exception)
        {
            type = null;
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            error = "消息缺少 type 字段（或它不是字符串）。";
            return false;
        }

        message = new WebPanelMessage(type!, obj["payload"]?.ToJsonString());
        error = null;
        return true;
    }
}
