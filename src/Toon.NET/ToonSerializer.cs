#nullable enable
using System;
using System.Text.Json;

namespace Toon
{
    /// <summary>
    /// 提供与 System.Text.Json 风格一致的 TOON 编解码入口，统一由 <see cref="ToonSerializerOptions"/> 控制行为。
    /// Serialize 路径: .NET 对象 -> JsonElement -> TOON 文本
    /// Deserialize 路径: TOON 文本 -> JSON 字符串/DOM -> 目标类型
    /// </summary>
    public static class ToonSerializer
    {
        /// <summary>将 .NET 对象编码为 TOON 文本。</summary>
        public static string Serialize<T>(T value, ToonSerializerOptions? options = null)
        {
            options ??= ToonSerializerOptions.Default;
            var element = JsonSerializer.SerializeToElement(value, options.JsonOptions);
            return Internal.Encode.ToonEncoder.Encode(element, options);
        }

        /// <summary>将 .NET 对象（使用显式类型）编码为 TOON 文本。</summary>
        public static string Serialize(object? value, Type inputType, ToonSerializerOptions? options = null)
        {
            if (inputType is null) throw new ArgumentNullException(nameof(inputType));
            options ??= ToonSerializerOptions.Default;

            // 使用显式类型序列化为 JsonElement，以保持精度与转换器行为
            var element = JsonSerializer.SerializeToElement(value, inputType, options.JsonOptions);
            return Internal.Encode.ToonEncoder.Encode(element, options);
        }

        /// <summary>将 TOON 文本解码为 .NET 对象。</summary>
        public static T? Deserialize<T>(string toon, ToonSerializerOptions? options = null)
        {
            if (toon is null) throw new ArgumentNullException(nameof(toon));
            options ??= ToonSerializerOptions.Default;

            // 先解码为 JSON（字符串或 DOM），再交给 System.Text.Json
            var json = Internal.Decode.ToonDecoder.DecodeToJsonString(toon, options);
            return JsonSerializer.Deserialize<T>(json, options.JsonOptions);
        }

        /// <summary>将 TOON 文本解码为指定类型实例。</summary>
        public static object? Deserialize(string toon, Type returnType, ToonSerializerOptions? options = null)
        {
            if (toon is null) throw new ArgumentNullException(nameof(toon));
            if (returnType is null) throw new ArgumentNullException(nameof(returnType));
            options ??= ToonSerializerOptions.Default;

            var json = Internal.Decode.ToonDecoder.DecodeToJsonString(toon, options);
            return JsonSerializer.Deserialize(json, returnType, options.JsonOptions);
        }
    }
}

namespace Toon.Internal.Encode
{
    using Toon.Internal.Shared;

    /// <summary>
    /// 编码器入口：从 JsonElement 生成 TOON 文本。
    /// 该类的完整实现将对齐 TypeScript 版本的 encoders.ts/normalize.ts/primitives.ts/writer.ts。
    /// 当前为占位实现，后续提交中逐步替换。
    /// </summary>
    internal static class ToonEncoder
    {
        internal static string Encode(JsonElement element, Toon.ToonSerializerOptions options)
        {
            // TODO: 替换为完整实现：Normalize(JsonElement) -> Encoders -> Writer
            // 临时占位：尽快输出最小可用的 TOON 字面，用于打通端到端流程
            // - 原子值: string/number/bool/null
            // - 对象/数组: 先退化为 JSON 文本（方便后续单测迭代）
            if (element.ValueKind == JsonValueKind.Array || element.ValueKind == JsonValueKind.Object)
{
    return Encoders.EncodeValue(element, options);
}
return element.ValueKind switch
{
    JsonValueKind.String => Primitives.EncodeStringLiteral(element.GetString() ?? string.Empty, options),
    JsonValueKind.Number => Primitives.EncodePrimitive(element, options),
    JsonValueKind.True => "true",
    JsonValueKind.False => "false",
    JsonValueKind.Null => "null",
    _ => "null",
};
        }
    }
}

namespace Toon.Internal.Decode
{
    /// <summary>
    /// 解码器入口：从 TOON 文本生成 JSON 字符串。
    /// 该类的完整实现将对齐 TypeScript 版本的 scanner.ts/parser.ts/decoders.ts/validation.ts。
    /// 当前为占位实现，后续提交中逐步替换。
    /// </summary>
    internal static class ToonDecoder
    {
        internal static string DecodeToJsonString(string toon, Toon.ToonSerializerOptions options)
        {
            // TODO: 替换为完整实现：Scanner -> Parser/Decoders -> JSON DOM
            // 临时策略：
            // - 如果是单个原子值，直接映射为 JSON 原子
            // - 其它情况暂不处理，返回原文以便失败早发现
            var t = toon.Trim();
            if (t.Length == 0)
                throw new ArgumentException("Cannot decode empty input.", nameof(toon));

            // 简单原子判定（与 TS 行为一致性将在后续实现中完善）
            if (t == "null" || t == "true" || t == "false")
                return t;

            // 纯数字或有符号数字（保真）
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                return t;

            // 否则先按字符串处理（加入引号并简易转义）
            // 注：完整实现会严格遵循 parser 的字符串规则
            var escaped = t.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $"\"{escaped}\"";
        }
    }
}
