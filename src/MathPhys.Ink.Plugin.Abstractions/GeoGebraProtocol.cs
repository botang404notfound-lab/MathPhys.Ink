using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 面板与宿主之间的一条消息。
/// </summary>
/// <remarks>
/// 统一信封，<b>永远是 JSON 对象且带 <c>type</c> 字段</b>：
/// <code>
/// { "type": "ready",   "payload": { ... } }
/// { "type": "command", "payload": { ... } }
/// </code>
/// 约定这么死板是有原因的：两边（C# 与 JS）各写各的，一旦信封格式有歧义，
/// 表现就是"消息发出去了但对面没反应"，而且<b>两边都觉得自己是对的</b>。
/// </remarks>
public sealed class GeoGebraMessage
{
    /// <summary>构造一条消息。</summary>
    /// <param name="type">消息类型（小驼峰英文，见 <see cref="GeoGebraProtocol"/> 的常量）。</param>
    /// <param name="payloadJson">payload 的原始 JSON 文本；无 payload 时为 <c>null</c>。</param>
    public GeoGebraMessage(string type, string? payloadJson)
    {
        Type = type ?? string.Empty;
        PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "null" : payloadJson!;
    }

    /// <summary>消息类型（小驼峰英文）。</summary>
    public string Type { get; }

    /// <summary>payload 的原始 JSON 文本（无 payload 时为 <c>"null"</c>）。</summary>
    public string PayloadJson { get; }

    /// <summary>是否真的带了 payload（而不是 <c>null</c>）。</summary>
    public bool HasPayload
        => !string.Equals(PayloadJson, "null", StringComparison.Ordinal);

    /// <summary>
    /// 把 payload 反序列化成指定类型；失败返回 <c>default</c>（不抛）。
    /// </summary>
    /// <remarks>
    /// 解析失败一律返回 <c>default</c> 而不是抛异常：消息来自 JS（页面可能被改坏），
    /// 一条畸形消息不该把宿主带走。
    /// </remarks>
    public T? DeserializePayload<T>()
    {
        if (!HasPayload) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(PayloadJson);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// 读 payload 里某个字符串字段（如 <c>error</c> 消息的 <c>message</c>）；取不到返回 <c>null</c>。
    /// </summary>
    public string? ReadPayloadString(string property)
    {
        if (!HasPayload || string.IsNullOrEmpty(property)) return null;

        try
        {
            return JsonNode.Parse(PayloadJson)?[property]?.GetValue<string>();
        }
        catch (Exception)
        {
            // 字段不是字符串、payload 不是对象 —— 都当"没有"处理。
            return null;
        }
    }

    /// <summary>便于日志与断言的一行摘要。</summary>
    public override string ToString()
        => HasPayload ? $"{Type} {PayloadJson}" : Type;
}

/// <summary>
/// 消息信封的编解码 + 类型常量（M7.5）。
/// </summary>
/// <remarks>
/// 编解码<b>单独放契约层</b>而不是塞进 WebView2 后端，是为了让验收 harness
/// 能在没有 WebView2 的环境里断言"两边说的一样"（见 <c>RunM75Checks</c>）。
/// </remarks>
public static class GeoGebraProtocol
{
    // ---------------------------------------------------------------- 宿主 → JS

    /// <summary>打开面板时带素材（payload: <c>{ ggbBase64 }</c>）。</summary>
    public const string LoadMaterial = "loadMaterial";

    /// <summary>让面板执行一条命令（payload 由素材决定）。</summary>
    public const string Command = "command";

    /// <summary>重置为空白工作区。</summary>
    public const string Reset = "reset";

    /// <summary>要求面板导出当前图形为 PNG（前端回 <see cref="ExportPng"/>）。</summary>
    public const string RequestExportPng = "requestExportPng";

    /// <summary>
    /// 宿主在"重新挂载"（关闭再打开 / 全屏⇄停靠切换）后向页面打招呼；
    /// 页面应重发一条 <see cref="Ready"/>（页面试图：本体一直在跑，但宿主侧的就绪闸门复位了）。
    /// </summary>
    public const string Hello = "hello";

    // ---------------------------------------------------------------- JS → 宿主

    /// <summary>页面初始化完成（★ 就绪闸门的放行信号）。</summary>
    public const string Ready = "ready";

    /// <summary>图形变了（可选上报，宿主侧按 ≥200ms 合并）。</summary>
    public const string Changed = "changed";

    /// <summary>面板里出错了。★ 所有 JS 异常都必须回这条，不许静默失败。</summary>
    public const string Error = "error";

    /// <summary>
    /// 应用没能载入（页面自己报的，payload 带中文 <c>reason</c> 与可选 <c>detail</c>）。
    /// </summary>
    /// <remarks>
    /// ★ 收到它必须<b>倒掉待发队列</b>并停止继续排队。
    /// 没有这条消息的话，加载失败时宿主的命令会一直攒着等一个永远不来的
    /// <see cref="Ready"/> —— 用户看到的就是这个项目最怕的「点了没反应」，
    /// 而且日志里连一条错误都没有。
    /// </remarks>
    public const string LoadFailed = "loadFailed";

    /// <summary>
    /// 面板自检结果（payload 带 <c>objects</c> 与 <c>names</c>）。
    /// </summary>
    /// <remarks>
    /// 仅用于发布冒烟：面板页在 <c>?selftest=1</c> 下画一个圆 + 一个点，
    /// 把"到底建出了几个对象"回传给宿主，宿主写进日志 ——
    /// 这样"断网可画圆/拖点"不再只靠看截图，而是有一条可断言的日志证据。
    /// 正常使用不会发这条消息。
    /// </remarks>
    public const string SelfTest = "selfTest";

    /// <summary>
    /// 面板内部日志（仅发布冒烟用，<c>?selftest=1</c> 时回传）。
    /// </summary>
    /// <remarks>正常面板产生的日志量不小，只在自测时回传，避免污染正式运行日志。</remarks>
    public const string Diagnostic = "diagnostic";

    /// <summary>导出结果（payload: <c>{ dataUrl }</c>）。</summary>
    public const string ExportPng = "exportPng";

    /// <summary>把一条消息编码成 JSON 文本。</summary>
    /// <param name="type">消息类型。空或空白会抛 <see cref="ArgumentException"/>（写错了要立刻知道）。</param>
    /// <param name="payload">
    /// payload。可以是任意可序列化对象、<see cref="JsonNode"/>（原样嵌入），
    /// 或 <c>null</c>（不带 payload）。<b>字符串按"字符串值"处理</b>，
    /// 要传原始 JSON 请传 <see cref="JsonNode"/>。
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
    public static bool TryDecode(string? json, out GeoGebraMessage message, out string? error)
    {
        message = new GeoGebraMessage(string.Empty, null);

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

        message = new GeoGebraMessage(type!, obj["payload"]?.ToJsonString());
        error = null;
        return true;
    }
}
