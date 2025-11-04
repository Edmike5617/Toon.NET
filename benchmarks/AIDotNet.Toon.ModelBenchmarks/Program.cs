using System.Collections.Concurrent;
using System.Text;
using Spectre.Console;
using System.Text.Json;
using System.Globalization;
using YamlDotNet.Serialization;
using AIDotNet.Toon;
using Spectre.Console.Rendering;

namespace AIDotNet.Toon.ModelBenches;

internal static class Program
{
    private static readonly global::AIDotNet.Toon.ModelBenches.BenchmarkFormat[] Formats = new[]
    {
        global::AIDotNet.Toon.ModelBenches.BenchmarkFormat.Toon,
        global::AIDotNet.Toon.ModelBenches.BenchmarkFormat.JsonPretty,
        global::AIDotNet.Toon.ModelBenches.BenchmarkFormat.Yaml,
        global::AIDotNet.Toon.ModelBenches.BenchmarkFormat.JsonCompact
    };

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("模型格式准确性基准（.NET）");

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
        {
            Console.WriteLine("[错误] 缺少 OPENAI_API_KEY 环境变量，请先设置后再运行。");
            return;
        }

        var models = GetModels();
        var tasks = TaskCatalog.All();
        var runs = GetRuns();
        var totalStepsPerModel = tasks.Count * runs * Formats.Length;
        var allModelResults = new ConcurrentBag<ModelResults>();

        // 启动一个后台 Live 面板，用于实时刷新汇总信息
        using var summaryCts = new CancellationTokenSource();
        var liveTask = Task.Run(() =>
        {
            try
            {
                AnsiConsole.Live(RenderSummaryRenderable(allModelResults))
                    .Start(ctx =>
                    {
                        while (!summaryCts.Token.IsCancellationRequested)
                        {
                            ctx.UpdateTarget(RenderSummaryRenderable(allModelResults));
                            Thread.Sleep(400);
                        }
                    });
            }
            catch { }
        });

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new ElapsedTimeColumn(),
                new SpinnerColumn()
            })
            .StartAsync(async ctx =>
            {
                var modelThrottle = new SemaphoreSlim(initialCount: GetModelParallelism());
                var modelJobs = new List<Task>();

                foreach (var model in models)
                {
                    await modelThrottle.WaitAsync();
                    var progressTask = ctx.AddTask($"[blue]{model}[/]", autoStart: true, maxValue: totalStepsPerModel);
                    var job = Task.Run(async () =>
                    {
                        try
                        {
                            var modelResult = await RunModelAsync(model, tasks, runs, totalSteps: totalStepsPerModel, progressTask);
                            allModelResults.Add(modelResult);
                        }
                        finally
                        {
                            modelThrottle.Release();
                        }
                    });
                    modelJobs.Add(job);
                }

                await Task.WhenAll(modelJobs);
            });

        // 停止实时面板刷新并等待任务结束
        summaryCts.Cancel();
        try { await liveTask.ConfigureAwait(false); } catch { }

        // 最终输出一次完整的汇总面板
        RenderSummaryPanel(allModelResults.ToList());

        // Generate single unified report with all models
        var outDir = EnsureResultsDir();
        var reportPath = Path.Combine(outDir, "benchmark-report.html");
        ReportGenerator.GenerateUnifiedHtml(allModelResults.ToList(), reportPath);
        AnsiConsole.MarkupLine($"[green]综合报告已保存至[/] [link]{Path.GetRelativePath(GetRepoRoot(), reportPath).Replace('\\', '/')}[/]");
    }

    private static async Task RunOneAsync(global::AIDotNet.Toon.ModelBenches.ModelClient client, global::AIDotNet.Toon.ModelBenches.BenchmarkTask t, object input, global::AIDotNet.Toon.ModelBenches.BenchmarkFormat fmt, ConcurrentBag<global::AIDotNet.Toon.ModelBenches.SingleResult> sink)
    {
        try
        {
            var formatted = Formatters.Format(fmt, input);
            var lang = Formatters.CodeFenceLanguage(fmt);

            var system = new ChatMessage("system", "You are a precise assistant. Follow the user instructions exactly and return only the requested answer.");
            var user = new ChatMessage("user",
                $"Instruction: {t.Instruction}\n\nFormat: {Formatters.DisplayName(fmt)}\n\nInput:\n```{lang}\n{formatted}\n```\n\nReturn only the final answer with no explanation.");

            var resp = await client.ChatAsync(new[] { system, user });

            // 输出格式合规性（不影响原有正确性评分）
            var formatOk = TryNormalizeScalar(fmt, resp.Text, out var _);

            var correct = t.Scorer(resp.Text, input);
            sink.Add(new SingleResult
            {
                TaskId = t.Id,
                TaskName = t.Name,
                FormatDisplay = Formatters.DisplayName(fmt),
                Correct = correct,
                FormatValid = formatOk,
                PromptTokens = resp.PromptTokens,
                CompletionTokens = resp.CompletionTokens,
                TotalTokens = resp.TotalTokens,
                Answer = resp.Text
            });

            // Avoid noisy per-request logging; Spectre progress shows completion
        }
        catch (Exception ex)
        {
            sink.Add(new SingleResult
            {
                TaskId = t.Id,
                TaskName = t.Name,
                FormatDisplay = Formatters.DisplayName(fmt),
                Correct = false,
                PromptTokens = 0,
                CompletionTokens = 0,
                TotalTokens = 0,
                Answer = $"[error] {ex.GetType().Name}: {ex.Message}"
            });
            // Keep console quiet here to preserve progress layout
        }
    }

    private static async Task<ModelResults> RunModelAsync(string model, IReadOnlyList<global::AIDotNet.Toon.ModelBenches.BenchmarkTask> tasks, int runs, int totalSteps, Spectre.Console.ProgressTask progressTask)
    {
        var results = new ConcurrentBag<global::AIDotNet.Toon.ModelBenches.SingleResult>();
        using var client = new ModelClient(model);
        var throttle = new SemaphoreSlim(initialCount: GetParallelism());
        var jobs = new List<Task>();

        int done = 0, ok = 0, fail = 0;
        var lastUpdateTime = DateTime.UtcNow;
        var updateLock = new object();

        foreach (var task in tasks)
        {
            for (int run = 0; run < runs; run++)
            {
                var input = task.BuildInput();
                foreach (var fmt in Formats)
                {
                    await throttle.WaitAsync();
                    jobs.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await RunOneAsync(client, task, input, fmt, results);
                        }
                        finally
                        {
                            throttle.Release();
                            var last = Interlocked.Increment(ref done);

                            // 限制更新频率：每200ms更新一次，或者每完成10%任务更新一次
                            bool shouldUpdate = false;
                            lock (updateLock)
                            {
                                var now = DateTime.UtcNow;
                                var elapsed = (now - lastUpdateTime).TotalMilliseconds;
                                if (elapsed >= 200 || last % Math.Max(1, totalSteps / 10) == 0 || last == totalSteps)
                                {
                                    lastUpdateTime = now;
                                    shouldUpdate = true;
                                }
                            }

                            if (shouldUpdate)
                            {
                                // 批量计算统计信息
                                var snapOk = results.Count(r => r.Correct);
                                var snapFail = results.Count(r => !r.Correct && !r.Answer.StartsWith("[error]"));
                                var snapErr = results.Count(r => r.Answer.StartsWith("[error]"));
                                ok = snapOk;
                                fail = snapFail + snapErr;

                                progressTask.Value = last;
                                progressTask.Description = $"[blue]{model}[/]  ok:[green]{ok}[/]  fail:[red]{fail}[/]  done:{last}/{totalSteps}";
                            }
                        }
                    }));
                }
            }
        }

        await Task.WhenAll(jobs);

        var grouped = results.GroupBy(r => r.FormatDisplay).OrderBy(g => g.Key).ToArray();
        var summary = grouped.Select(g => new global::AIDotNet.Toon.ModelBenches.FormatSummary
        {
            Format = ParseFormat(g.Key),
            FormatDisplay = g.Key,
            Accuracy = g.Average(r => r.Correct ? 1.0 : 0.0),
            FormatValidity = g.Average(r => r.FormatValid ? 1.0 : 0.0),
            AvgPromptTokens = g.Average(r => r.PromptTokens),
            AvgCompletionTokens = g.Average(r => r.CompletionTokens)
        }).ToList();

        // 不在此处打印表格（由主流程统一呈现），返回模型结果

        return new ModelResults
        {
            Model = model,
            Results = results.OrderBy(r => r.TaskName).ThenBy(r => r.FormatDisplay).ToList(),
            Summary = summary
        };
    }

    // 返回一个可用于 Live 更新的 IRenderable（面板）
    private static IRenderable RenderSummaryRenderable(IEnumerable<ModelResults> allResults)
    {
        var table = new Table().Title("[yellow]模型汇总（按模型）[/]").AddColumns("模型", "平均准确率 %", "平均合规率 %", "平均提示 Tokens", "平均生成 Tokens");
        foreach (var mr in allResults.OrderBy(m => m.Model))
        {
            var avgAcc = mr.Summary.Any() ? mr.Summary.Average(s => s.Accuracy) * 100.0 : 0.0;
            var avgValid = mr.Summary.Any() ? mr.Summary.Average(s => s.FormatValidity) * 100.0 : 0.0;
            var avgPrompt = mr.Summary.Any() ? mr.Summary.Average(s => s.AvgPromptTokens) : 0.0;
            var avgComp = mr.Summary.Any() ? mr.Summary.Average(s => s.AvgCompletionTokens) : 0.0;
            table.AddRow(mr.Model, avgAcc.ToString("0.0"), avgValid.ToString("0.0"), avgPrompt.ToString("0.0"), avgComp.ToString("0.0"));
        }
        return new Panel(table).Expand();
    }

    // 在主流程打印单一的汇总面板（在所有模型完成或部分完成后更新）
    // 此方法用于生成并输出最终的汇总面板
    private static void RenderSummaryPanel(IEnumerable<ModelResults> allResults)
    {
        AnsiConsole.Write(RenderSummaryRenderable(allResults));
    }

    private static string EnsureResultsDir()
    {
        var root = GetRepoRoot();
        var dir = Path.Combine(root, "benchmarks", "AIDotNet.Toon.ModelBenchmarks", "results");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetRepoRoot()
    {
        // Walk up from BaseDirectory to find the solution marker
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var marker = Path.Combine(dir, "AIDotNet.Toon.sln");
            if (File.Exists(marker))
                return dir;

            var parent = Directory.GetParent(dir)?.FullName;
            if (string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                break;
            dir = parent ?? string.Empty;
        }
        return Directory.GetCurrentDirectory();
    }

    private static BenchmarkFormat ParseFormat(string display)
    {
        return display.ToLowerInvariant() switch
        {
            "toon" => BenchmarkFormat.Toon,
            "json" => BenchmarkFormat.JsonPretty,
            "json compact" => BenchmarkFormat.JsonCompact,
            "yaml" => BenchmarkFormat.Yaml,
            _ => BenchmarkFormat.JsonPretty
        };
    }

    private static string[] GetModels()
    {
        // Prefer OPENAI_MODELS as comma-separated list; fallback to OPENAI_MODEL single value
        var list = Environment.GetEnvironmentVariable("OPENAI_MODELS");
        if (!string.IsNullOrWhiteSpace(list))
        {
            return list
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        var single = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
        return new[] { single };
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var arr = s.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(arr);
    }



    private static int GetRuns()
    {
        var v = Environment.GetEnvironmentVariable("BENCHMARK_RUNS");
        if (int.TryParse(v, out var n))
        {
            return Math.Clamp(n, 1, 10);
        }
        return 1;
    }

    private static int GetParallelism()
    {
        var v = Environment.GetEnvironmentVariable("BENCHMARK_PARALLELISM");
        if (int.TryParse(v, out var n))
        {
            return Math.Clamp(n, 1, 16);
        }
        // default moderate concurrency to speed up without being too aggressive
        return 6;
    }

    private static int GetModelParallelism()
    {
        var v = Environment.GetEnvironmentVariable("BENCHMARK_MODEL_PARALLELISM");
        if (int.TryParse(v, out var n))
        {
            return Math.Clamp(n, 1, 8);
        }
        return 2; // reasonable default
    }

    // ===== 输出格式合规性检测（不改变原有评分，只做额外分类统计）=====
    private static bool TryNormalizeScalar(BenchmarkFormat fmt, string raw, out string normalized)
    {
        normalized = string.Empty;
        if (raw is null) return false;

        var payload = ExtractCodePayload(raw).Trim();
        if (payload.Length == 0) return false;

        return fmt switch
        {
            BenchmarkFormat.JsonPretty => TryParseJsonScalar(payload, out normalized),
            BenchmarkFormat.JsonCompact => TryParseJsonScalar(payload, out normalized),
            BenchmarkFormat.Yaml => TryParseYamlScalar(payload, out normalized),
            BenchmarkFormat.Toon => TryParseToonScalar(payload, out normalized),
            _ => false
        };
    }

    // 支持 ```lang ... ``` 代码块，或直接纯文本
    private static string ExtractCodePayload(string s)
    {
        var txt = (s ?? string.Empty).Trim();
        const string fence = "```";
        int i = txt.IndexOf(fence, StringComparison.Ordinal);
        if (i < 0) return txt;

        int j = txt.IndexOf(fence, i + fence.Length, StringComparison.Ordinal);
        if (j < 0) return txt;

        var inner = txt.Substring(i + fence.Length, j - (i + fence.Length));
        // 去掉可选语言行
        var nl = inner.IndexOf('\n');
        if (nl >= 0)
            inner = inner.Substring(nl + 1);
        return inner.Trim();
    }

    private static bool TryParseJsonScalar(string s, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.String:
                    normalized = root.GetString() ?? string.Empty; return true;
                case JsonValueKind.Number:
                    normalized = root.GetRawText(); return true;
                case JsonValueKind.True:
                    normalized = "true"; return true;
                case JsonValueKind.False:
                    normalized = "false"; return true;
                case JsonValueKind.Null:
                    normalized = "null"; return true;
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    private static bool TryParseYamlScalar(string s, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            var des = new DeserializerBuilder().Build();
            var obj = des.Deserialize<object>(s);

            if (obj is null) { normalized = "null"; return true; }
            if (obj is string str) { normalized = str.Trim(); return true; }
            if (obj is bool b) { normalized = b ? "true" : "false"; return true; }

            switch (obj)
            {
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    normalized = Convert.ToString(obj, CultureInfo.InvariantCulture) ?? string.Empty; return true;
                case float or double or decimal:
                    normalized = Convert.ToString(obj, CultureInfo.InvariantCulture) ?? string.Empty; return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool TryParseToonScalar(string s, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            var elem = ToonSerializer.Deserialize<JsonElement>(s, new ToonSerializerOptions());
            switch (elem.ValueKind)
            {
                case JsonValueKind.String:
                    normalized = elem.GetString() ?? string.Empty; return true;
                case JsonValueKind.Number:
                    normalized = elem.GetRawText(); return true;
                case JsonValueKind.True:
                    normalized = "true"; return true;
                case JsonValueKind.False:
                    normalized = "false"; return true;
                case JsonValueKind.Null:
                    normalized = "null"; return true;
                default:
                    return false;
            }
        }
        catch { return false; }
    }
}