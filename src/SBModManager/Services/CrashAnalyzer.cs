using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>
/// Collects crash evidence (WER report, crash dump, UE/UE4SS logs) and turns it into a short,
/// human-readable analysis with likely causes tied to the installed mods.
/// </summary>
public static class CrashAnalyzer
{
    private static readonly (string Code, string Meaning, string Advice)[] KnownCodes =
    {
        ("c0000005", "访问冲突（Access Violation）", "常见于模组与游戏版本不匹配、PAK 冲突或 UE4SS 脚本异常。建议先停用最近安装的模组再试。"),
        ("c000001d", "非法指令", "CPU 指令异常，通常是注入的 DLL（如 UE4SS 版本不匹配）导致。"),
        ("c0000094", "整数除零", "模组数据异常导致的计算错误。"),
        ("c00000fd", "栈溢出", "递归调用过深，常见于脚本模组死循环。"),
        ("80000003", "断点/断言失败", "游戏触发了内部断言，多为资源或模组数据不合法。"),
        ("e06d7363", "C++ 异常", "未捕获的 C++ 异常，常由模组或 UE4SS 插件抛出。"),
        ("c0000409", "栈缓冲区溢出", "安全校验失败，可能是注入或反作弊冲突。"),
        ("c0000420", "断言失败", "游戏断言失败，通常是资源缺失或版本不匹配。"),
        ("d1", "驱动相关异常", "显卡驱动或覆盖层（RTSS/Afterburner）冲突。"),
    };

    public static CrashReport? Build(DateTimeOffset since, int? exitCode)
    {
        var wer = FindWerReport(since);
        var dump = FindDump(since);
        var logTail = ReadGameLogTail();

        var code = wer?.ExceptionCode ?? "";
        var module = wer?.FaultModule ?? "";
        var evidence = wer is not null || dump is not null ||
                       logTail.Contains("Fatal error", StringComparison.OrdinalIgnoreCase) ||
                       logTail.Contains("Unhandled Exception", StringComparison.OrdinalIgnoreCase);

        // A clean exit (code 0) without crash evidence is not a crash.
        if (!evidence && exitCode is 0 or null) return null;
        if (!evidence && exitCode is not null && exitCode.Value != 0)
        {
            // Non-zero exit without a WER report: still worth a short note.
        }
        else if (!evidence)
        {
            return null;
        }

        var analysis = new StringBuilder();
        var known = KnownCodes.FirstOrDefault(k =>
            !string.IsNullOrEmpty(code) && code.Contains(k.Code, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(known.Meaning))
        {
            analysis.AppendLine($"异常代码：0x{code} — {known.Meaning}");
            analysis.AppendLine(known.Advice);
        }
        else if (!string.IsNullOrEmpty(code))
        {
            analysis.AppendLine($"异常代码：0x{code}");
        }

        if (!string.IsNullOrEmpty(module))
        {
            analysis.AppendLine($"出错模块：{module}");
            if (module.Contains("d3d", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("dxgi", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("nvwgf", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("amdvlk", StringComparison.OrdinalIgnoreCase))
            {
                analysis.AppendLine("指向显卡驱动/图形层：建议关闭超频、覆盖层（RTSS、NVIDIA App），或更新显卡驱动。");
            }
            else if (module.Contains("ue4ss", StringComparison.OrdinalIgnoreCase) ||
                     module.Contains("dwmapi", StringComparison.OrdinalIgnoreCase))
            {
                analysis.AppendLine("指向 UE4SS：确认 UE4SS 与游戏版本匹配，并逐个停用脚本模组排查。");
            }
        }

        if (exitCode is not null)
        {
            analysis.AppendLine($"退出代码：{exitCode}（0x{exitCode.Value:X8}）");
        }

        var modHints = AnalyzeMods();
        if (modHints.Count > 0)
        {
            analysis.AppendLine();
            analysis.AppendLine("可能相关的模组：");
            foreach (var hint in modHints.Take(8)) analysis.AppendLine("· " + hint);
        }

        if (logTail.Length > 0)
        {
            analysis.AppendLine();
            analysis.AppendLine("游戏日志末尾：");
            analysis.AppendLine(logTail);
        }

        var summary = !string.IsNullOrEmpty(known.Meaning)
            ? known.Meaning
            : !string.IsNullOrEmpty(code) ? "游戏异常退出（0x" + code + "）" : "游戏异常退出";

        var report = new CrashReport
        {
            Summary = summary,
            Analysis = analysis.ToString().TrimEnd(),
            ExceptionCode = code,
            FaultModule = module,
            DumpPath = dump,
            WerReportPath = wer?.Path,
            LogTail = logTail,
            ReportPath = "",
        };

        report = new CrashReport
        {
            Summary = report.Summary,
            Analysis = report.Analysis,
            ExceptionCode = report.ExceptionCode,
            FaultModule = report.FaultModule,
            DumpPath = report.DumpPath,
            WerReportPath = report.WerReportPath,
            LogTail = report.LogTail,
            ReportPath = Save(report),
        };
        return report;
    }

    private sealed class WerInfo
    {
        public string Path { get; init; } = "";
        public string ExceptionCode { get; init; } = "";
        public string FaultModule { get; init; } = "";
    }

    private static WerInfo? FindWerReport(DateTimeOffset since)
    {
        try
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "WER", "ReportQueue"),
            };

            foreach (var root in roots.Where(Directory.Exists))
            {
                foreach (var dir in Directory.EnumerateDirectories(root)
                             .Select(d => new DirectoryInfo(d))
                             .Where(d => d.LastWriteTime >= since.LocalDateTime.AddMinutes(-2))
                             .OrderByDescending(d => d.LastWriteTime))
                {
                    if (!dir.Name.Contains("SB", StringComparison.OrdinalIgnoreCase) &&
                        !dir.Name.Contains("Stellar", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var reportFile = Path.Combine(dir.FullName, "Report.wer");
                    if (!File.Exists(reportFile)) continue;

                    var code = "";
                    var module = "";
                    foreach (var line in ReadWerLines(reportFile))
                    {
                        if (line.StartsWith("ExceptionCode=", StringComparison.OrdinalIgnoreCase))
                        {
                            code = line[(line.IndexOf('=') + 1)..].Trim();
                        }
                        else if (line.StartsWith("FaultingModule=", StringComparison.OrdinalIgnoreCase))
                        {
                            module = line[(line.IndexOf('=') + 1)..].Trim();
                        }
                    }

                    return new WerInfo { Path = dir.FullName, ExceptionCode = code, FaultModule = module };
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取 WER 报告失败: " + ex.Message);
        }
        return null;
    }

    /// <summary>WER writes Report.wer as UTF-16LE (sometimes with a BOM).</summary>
    private static IEnumerable<string> ReadWerLines(string path)
    {
        string text;
        try
        {
            var bytes = File.ReadAllBytes(path);
            text = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
                ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
                : bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
                    ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
                    : Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            yield break;
        }

        foreach (var raw in text.Split('\n'))
        {
            yield return raw.TrimEnd('\r');
        }
    }

    private static string? FindDump(DateTimeOffset since)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrashDumps");
            if (!Directory.Exists(root)) return null;

            var dump = new DirectoryInfo(root).EnumerateFiles("*.dmp")
                .Where(f => f.LastWriteTime >= since.LocalDateTime.AddMinutes(-2))
                .Where(f => f.Name.StartsWith("SB", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.StartsWith("Stellar", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            return dump?.FullName;
        }
        catch (Exception ex)
        {
            Log.Warn("查找崩溃转储失败: " + ex.Message);
            return null;
        }
    }

    private static string ReadGameLogTail()
    {
        try
        {
            var candidates = new List<string>();
            var gameDir = AppState.Instance.GameDir;
            if (gameDir is not null)
            {
                candidates.Add(Path.Combine(gameDir, "SB", "Saved", "Logs", "SB.log"));
                candidates.Add(Path.Combine(gameDir, "SB", "Saved", "Logs", "SB-Win64-Shipping.log"));
                candidates.Add(Path.Combine(gameDir, "SB", "Binaries", "Win64", "UE4SS.log"));
            }

            var log = candidates.FirstOrDefault(File.Exists);
            if (log is null) return "";

            var lines = File.ReadAllLines(log);
            var relevant = lines
                .Where(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("Fatal", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("Warning", StringComparison.OrdinalIgnoreCase))
                .TakeLast(12)
                .ToList();
            if (relevant.Count == 0) relevant = lines.TakeLast(8).ToList();
            return string.Join(Environment.NewLine, relevant);
        }
        catch (Exception ex)
        {
            Log.Warn("读取游戏日志失败: " + ex.Message);
            return "";
        }
    }

    private static List<string> AnalyzeMods()
    {
        var hints = new List<string>();
        try
        {
            var state = AppState.Instance;
            var enabled = state.Library.Where(m => state.IsEnabled(m.Id)).ToList();

            foreach (var mod in enabled.Where(m => m.Type == ModType.Ue4ss).Take(4))
            {
                hints.Add($"脚本模组「{mod.Name}」— UE4SS 脚本异常是常见崩溃源，可先停用它");
            }

            foreach (var group in enabled
                         .Where(m => !string.IsNullOrWhiteSpace(m.GroupKey))
                         .GroupBy(m => m.GroupKey, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                hints.Add($"同一模组的多个版本同时启用：{string.Join(" / ", group.Select(m => m.Name))}");
            }

            var recent = enabled
                .Where(m => m.ImportedAt > DateTimeOffset.Now.AddDays(-3))
                .OrderByDescending(m => m.ImportedAt)
                .Take(3);
            foreach (var mod in recent)
            {
                hints.Add($"最近安装「{mod.Name}」— 若崩溃在安装后出现，优先停用它");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("分析模组失败: " + ex.Message);
        }
        return hints;
    }

    private static string Save(CrashReport report)
    {
        try
        {
            var dir = Path.Combine(AppPaths.Root, "crashes");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"game-crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var text = new StringBuilder()
                .AppendLine($"时间: {report.DetectedAt:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"结论: {report.Summary}")
                .AppendLine($"异常代码: {report.ExceptionCode}")
                .AppendLine($"出错模块: {report.FaultModule}")
                .AppendLine($"WER 报告: {report.WerReportPath ?? "(无)"}")
                .AppendLine($"崩溃转储: {report.DumpPath ?? "(无)"}")
                .AppendLine()
                .AppendLine(report.Analysis)
                .ToString();
            File.WriteAllText(path, text, Encoding.UTF8);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn("保存崩溃报告失败: " + ex.Message);
            return "";
        }
    }
}
