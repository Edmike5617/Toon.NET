# Toon.NET

Token-Oriented Object Notation 的 .NET 实现，设计参考 System.Text.Json 风格：
- 使用 System.Text.Json 作为统一 JSON 层
- 暴露统一的选项 [C#.ToonSerializerOptions](src/Toon.NET/ToonSerializerOptions.cs:28)
- 提供与 `JsonSerializer` 类似的 API：[C#.ToonSerializer](src/Toon.NET/ToonSerializer.cs:12)

当前版本专注于高性能编码路径，解码扫描/解析处于规划阶段。

## 安装

NuGet（待发布）：
```
dotnet add package Toon.NET
```

源代码集成：
- 将 `src/Toon.NET` 加入解决方案或作为子模块引用
- 推荐在项目文件中配置 `PackageReadmeFile` 指向本 README

## 快速开始

序列化为 TOON：
```csharp
using Toon;

var options = new ToonSerializerOptions
{
    Indent = 2,
    Delimiter = ToonDelimiter.Comma,
    Strict = true,
    LengthMarker = null
};

var data = new
{
    users = new[]
    {
        new { name = "alice", age = 30 },
        new { name = "bob", age = 25 }
    },
    tags = new[] { "a", "b", "c" },
    numbers = new[] { 1, 2, 3 }
};

string toonText = ToonSerializer.Serialize(data, options);
// 例如：
// users[2]{name,age}:
//   alice,30
//   bob,25
// tags[3]: a,b,c
// numbers[3]: 1,2,3
```

从 TOON 反序列化为 .NET（解码模块仍为占位，原子值工作正常，复杂结构将跟进）：
```csharp
var value = ToonSerializer.Deserialize<string>("hello", options); // "hello"
var number = ToonSerializer.Deserialize<double>("3.1415", options); // 3.1415
```

## System.Text.Json 风格统一选项

选项类：[C#.ToonSerializerOptions](src/Toon.NET/ToonSerializerOptions.cs:28)

- Indent: 每级缩进空格数，默认 2
- Delimiter: 分隔符，枚举 [C#.ToonDelimiter](src/Toon.NET/ToonSerializerOptions.cs:10)（Comma/Tab/Pipe）
- Strict: 解码严格模式（缩进/行数/空行/多余项等校验），默认 true
- LengthMarker: 数组长度标记（仅支持 `#` 或 null），默认 null
- JsonOptions: 直通 System.Text.Json 的 [C#.JsonSerializerOptions](src/Toon.NET/ToonSerializerOptions.cs:42)，默认启用 `AllowNamedFloatingPointLiterals` 并注册写出转换器将 `NaN/±Infinity` 归一化为 `null`

默认实例：[C#.ToonSerializerOptions.Default](src/Toon.NET/ToonSerializerOptions.cs:99)

## 公共 API

- 序列化（泛型）：[C#.ToonSerializer.Serialize()](src/Toon.NET/ToonSerializer.cs:15)
- 序列化（显式类型）：[C#.ToonSerializer.Serialize()](src/Toon.NET/ToonSerializer.cs:23)
- 反序列化（泛型）：[C#.ToonSerializer.Deserialize()](src/Toon.NET/ToonSerializer.cs:34)
- 反序列化（显式类型）：[C#.ToonSerializer.Deserialize()](src/Toon.NET/ToonSerializer.cs:45)

内部桥接：
- 编码入口：[C#.ToonEncoder.Encode()](src/Toon.NET/ToonSerializer.cs:68)
- 解码入口：[C#.ToonDecoder.DecodeToJsonString()](src/Toon.NET/ToonSerializer.cs:100)

## 编码器设计与规则

入口：[C#.Encoders.EncodeValue()](src/Toon.NET/Internal/Encode/Encoders.cs:19)

对象编码：
- [C#.Encoders.EncodeObject()](src/Toon.NET/Internal/Encode/Encoders.cs:44) 按键顺序写行
- 键值对渲染：[C#.Encoders.EncodeKeyValuePair()](src/Toon.NET/Internal/Encode/Encoders.cs:52)
- 键名编码（不安全时加引号）：[C#.Primitives.EncodeKey()](src/Toon.NET/Internal/Encode/Primitives.cs:78)
- 空对象：仅输出 `key:` 行

数组编码：
- 原子数组使用行内格式 [C#.Encoders.EncodeInlineArrayLine()](src/Toon.NET/Internal/Encode/Encoders.cs:150)，头部由 [C#.Primitives.FormatHeader()](src/Toon.NET/Internal/Encode/Primitives.cs:109) 渲染
- 数组的数组（子数组均为原子数组）使用列表项展开：[C#.Encoders.EncodeArrayOfArraysAsListItems()](src/Toon.NET/Internal/Encode/Encoders.cs:135)
- 对象数组尽可能转为表格格式：
  - 检测表头：[C#.Encoders.ExtractTabularHeader()](src/Toon.NET/Internal/Encode/Encoders.cs:176)
  - 表格渲染：[C#.Encoders.EncodeArrayOfObjectsAsTabular()](src/Toon.NET/Internal/Encode/Encoders.cs:168)
  - 写数据行：[C#.Encoders.WriteTabularRows()](src/Toon.NET/Internal/Encode/Encoders.cs:215)
- 混合/复杂数组回退为列表格式：[C#.Encoders.EncodeMixedArrayAsListItems()](src/Toon.NET/Internal/Encode/Encoders.cs:235)

字符串与原子：
- 原子编码：[C#.Primitives.EncodePrimitive()](src/Toon.NET/Internal/Encode/Primitives.cs:30)
- 字符串不安全则加引号并转义：[C#.Primitives.EncodeStringLiteral()](src/Toon.NET/Internal/Encode/Primitives.cs:66)
- 数值：`NaN/±Infinity` 统一输出为 `null`；`-0` 规范化为 `0`；其余使用 `JsonElement.GetRawText()` 保持精度

行写入与缩进：
- 写入器：[C#.LineWriter](src/Toon.NET/Internal/Encode/Writer.cs:13)
- 行输出：[C#.LineWriter.Push()](src/Toon.NET/Internal/Encode/Writer.cs:32) 与 [C#.LineWriter.PushListItem()](src/Toon.NET/Internal/Encode/Writer.cs:42)

## 性能优化

本实现在不牺牲可读性的前提下进行若干微优化：

- 缩进缓存：
  - [C#.LineWriter.Push()](src/Toon.NET/Internal/Encode/Writer.cs:32) 使用 `_indentCache` 避免重复构造缩进字符串（替代 `Repeat` 每次拼接）
- 避免多余分配与 LINQ：
  - 用 `foreach`/枚举器替代 `Enumerable.All/Count/Select/ToList`：
    - 表头提取与行写：[C#.Encoders.ExtractTabularHeader()](src/Toon.NET/Internal/Encode/Encoders.cs:176)、[C#.Encoders.IsTabularArray()](src/Toon.NET/Internal/Encode/Encoders.cs:194)、[C#.Encoders.WriteTabularRows()](src/Toon.NET/Internal/Encode/Encoders.cs:215)
    - 对象列表项处理：[C#.Encoders.EncodeObjectAsListItem()](src/Toon.NET/Internal/Encode/Encoders.cs:246)
- StringBuilder 复用拼接：
  - 行内原子数组/表格数据行采用逐段 Append，避免 `string.Join` 构建中间集合：
    - [C#.Primitives.EncodeAndJoinPrimitives()](src/Toon.NET/Internal/Encode/Primitives.cs:89)
    - [C#.Encoders.WriteTabularRows()](src/Toon.NET/Internal/Encode/Encoders.cs:215)
- 数值保持原文：
  - 优先使用 `JsonElement.GetRawText()` 输出数值，避免 `TryGetDouble` 带来的解析与装箱
  - 文本化 `-0` 快速路径：[C#.Primitives.EncodePrimitive()](src/Toon.NET/Internal/Encode/Primitives.cs:30) 内部的负零文本检测
- 避免不必要的分支与分配：
  - 表格检测与写出路径呈线性扫描，尽量少中间集合
  - 列表项第一键处理不创建 `List<...>`，直接枚举器推进

建议配合 `Release` 构建与 R2R/ReadyToRun 发布提升启动性能。

## 与 TypeScript 规范的一致性

- 表格格式的字段名与值引号规则与 TS 对齐：
  - 字段名不安全（含空格/分隔符/数字开头等）将加引号
  - 值在当前分隔符上下文不安全时加引号
- 长度标记与分隔符头部：
  - `[N]` 或 `[#N]`，当分隔符不是逗号时头部方括号内附加分隔符字符
- 行缩进：
  - 默认 2 空格；表格数据行缩进一层，列表项使用 `- ` 前缀

## 基准测试建议

可使用 BenchmarkDotNet 构建简单基准（示例方案）：
- 两类工况：对象数组表格格式、混合数组列表格式
- 指标：吞吐（ops/s）、分配（B/op 与 Gen0 次数）
- 大小维度：元素数与字段数扩展
- 对比：不同分隔符与缩进设置对性能的影响

## 计划与路线图

- 解码扫描/解析/校验与完整 Decoders（对齐 TS 的 `scanner/parser/validation/decoders`）
- 归一化层（Normalize）：日期、特殊集合映射策略文档化
- 错误模型：严格模式下的定位与消息一致性（行列上下文）
- 文档完善与示例丰富，NuGet 包发布

## 贡献

欢迎提交 PR 与 Issue。建议遵循以下规范：
- 保持 System.Text.Json 风格的公共 API 与选项一致性
- 优先考虑可读性与实际收益的微优化
- 单测需覆盖新增路径与边界

## 许可

MIT
