using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MathPhys.Ink.Plugins;

/// <summary>
/// 通用 Web 面板与宿主之间的一条消息（M22）。
/// </summary>
/// <remarks>
/// 与 <see cref="GeoGebraMessage"/> <b>同构</b>（同一个信封格式），但去掉了 GeoGebra 专有语义：
/// 它服务于任何 Web 仿真页面。统一信封，<b>永远是 JSON 对象且带 <c>type</c> 字段</b>：
/// <code>
/// { "type": "ready", "payload": { ... } }
/// </code>
/// 为什么不直接复用 <see cref="GeoGebraMessage"/>：那会把"通用面板"和"GeoGebra 演示"
/// 两个概念绑死，读代码的人会以为仿真面板必须长成 GeoGebra 的样子。
/// </remarks>
public sealed class WebPanelMessage
{
    /// <summary>构造一条消息。</summary>
    /// <param name="type">消息类型（小驼峰英文，见 <see cref="WebPanelProtocol"/> 的常量）。</param>
    /// <param name="payloadJson">payload 的原始 JSON 文本；无 payload 时为 <c>null</c>。</param>
    public WebPanelMessage(string type, string? payloadJson)
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
    /// 解析失败一律返回 <c>default</c> 而不是抛异常：消息来自网页（页面可能被改坏），
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
