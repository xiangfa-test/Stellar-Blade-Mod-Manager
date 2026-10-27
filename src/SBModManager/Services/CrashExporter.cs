using System.IO.Compression;
using System.Text;
using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>
/// Bundles a crash report with its source evidence (WER report, dump, game log), the installed
/// mod list/environment and the manager log into one zip the user can attach to a bug report.
/// </summary>
public static class CrashExporter
{
    public static string Export(CrashReport report)
    {
        var dir = Path.Combine(AppPaths.Root, "crashes");
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, $"crash-export-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        WriteText(zip, "report.txt", BuildReportText(report));
        WriteText(zip, "disclaimer.txt",
            "本报告由「剑星 MOD 管理器」自动生成，仅针对已知可能的崩溃原因做初步评估，" +
            "不能完全确定问题所在，也不构成官方结论。" + Environment.NewLine +
            "请结合报告内容自行判断，或附上本压缩包反馈给模组作者。" + Environment.NewLine);

        if (!string.IsNullOrWhiteSpace(report.ReportPath) && File.Exists(report.ReportPath))
        {
            AddFile(zip, report.ReportPath, "evidence/report.txt");
        }

        if (!string.IsNullOrWhiteSpace(report.WerReportPath) && Directory.Exists(report.WerReportPath))
        {
            foreach (var file in Directory.EnumerateFiles(report.WerReportPath))
            {
                AddFile(zip, file, "evidence/wer/" + Path.GetFileName(file));
            }
        }

        if (!string.IsNullOrWhiteSpace(report.DumpPath) && File.Exists(report.DumpPath))
        {
            AddFile(zip, report.DumpPath, "evidence/dump/" + Path.GetFileName(report.DumpPath));
        }

        foreach (var log in GameLogs())
        {
            AddFile(zip, log, "evidence/game-logs/" + Path.GetFileName(log));
        }

        WriteText(zip, "environment.txt", BuildEnvironmentText());
        WriteText(zip, "mods.txt", BuildModsText());

        var managerLog = CurrentManagerLog();
        if (managerLog is not null)
        {
            AddFile(zip, managerLog, "manager-logs/" + Path.GetFileName(managerLog));
        }

        Log.Info("崩溃报告已导出: " + zipPath);
        return zipPath;
    }

    private static string BuildReportText(CrashReport report)
        => new StringBuilder()
            .AppendLine("=== 崩溃概要 ===")
            .AppendLine($"时间: {report.DetectedAt:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"结论: {report.Summary}")
            .AppendLine($"异常代码: {report.ExceptionCode}")
            .AppendLine($"出错模块: {report.FaultModule}")
            .AppendLine($"WER 报告: {report.WerReportPath ?? "(无)"}")
            .AppendLine($"崩溃转储: {report.DumpPath ?? "(无)"}")
            .AppendLine()
            .AppendLine("=== 分析 ===")
            .AppendLine(report.Analysis)
            .ToString();

    private static string BuildEnvironmentText()
    {
        var state = AppServices.State;
        var sb = new StringBuilder();
        sb.AppendLine("=== 管理器 / 系统 ===");
        sb.AppendLine($"管理器版本: {AppUpdater.CurrentVersion}");
        sb.AppendLine($"系统: {Environment.OSVersion}");
        sb.AppendLine($"64 位进程: {Environment.Is64BitProcess}");
        sb.AppendLine($"游戏目录: {state.GameDir ?? "(未设置)"}");
        sb.AppendLine($"数据目录: {AppPaths.Root}");
        sb.AppendLine($"已启用模组: {state.Library.Count(m => state.IsEnabled(m.Id))} / {state.Library.Count}");
        sb.AppendLine();
        sb.AppendLine("=== 游戏 ===");
        var gameDir = state.GameDir;
        if (gameDir is not null)
        {
            foreach (var exe in new[] { "SB.exe" })
            {
                var path = Path.Combine(gameDir, exe);
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    sb.AppendLine($"{exe}: {info.Length} 字节, 修改于 {info.LastWriteTime:yyyy-MM-dd HH:mm}");
                }
            }

            var ue4ss = Path.Combine(gameDir, "SB", "Binaries", "Win64", "ue4ss");
            sb.AppendLine($"UE4SS: {(Directory.Exists(ue4ss) ? "已安装" : "未安装")}");
            var dwmapi = Path.Combine(gameDir, "SB", "Binaries", "Win64", "dwmapi.dll");
            if (File.Exists(dwmapi))
            {
                var info = new FileInfo(dwmapi);
                sb.AppendLine($"dwmapi.dll: {info.Length} 字节, 修改于 {info.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
        }
        return sb.ToString();
    }

    private static string BuildModsText()
    {
        var state = AppServices.State;
        var sb = new StringBuilder();
        sb.AppendLine("=== 已安装模组（按优先级）===");
        foreach (var mod in state.Library.OrderByDescending(m => m.Priority))
        {
            sb.AppendLine(
                $"[{(state.IsEnabled(mod.Id) ? "启用" : "停用")}] " +
                $"优先级={mod.Priority} 类型={mod.DisplayType} 版本={mod.Version} " +
                $"导入={mod.ImportedAt.LocalDateTime:yyyy-MM-dd HH:mm} " +
                $"文件数={mod.Files.Count} 名称={mod.Name}" +
                (mod.NexusModId is > 0 ? $" nexus={mod.NexusModId}" : ""));
        }

        sb.AppendLine();
        sb.AppendLine("=== 最近安装（7 天）===");
        foreach (var mod in state.Library
                     .Where(m => m.ImportedAt > DateTimeOffset.Now.AddDays(-7))
                     .OrderByDescending(m => m.ImportedAt))
        {
            sb.AppendLine($"{mod.ImportedAt.LocalDateTime:yyyy-MM-dd HH:mm} {mod.Name} (v{mod.Version})");
        }

        sb.AppendLine();
        sb.AppendLine("=== 冲突/覆盖 ===");
        try
        {
            var gameDir = state.GameDir;
            if (gameDir is not null)
            {
                var report = Deployer.BuildConflictReport(gameDir);
                sb.AppendLine($"同名文件冲突链: {report.ConflictFileCount}");
                foreach (var chain in report.Chains.Take(20))
                {
                    sb.AppendLine($"  {chain.TargetPath} <- {chain.ChainText}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("冲突分析失败: " + ex.Message);
        }

        return sb.ToString();
    }

    private static IEnumerable<string> GameLogs()
    {
        var gameDir = AppServices.State.GameDir;
        if (gameDir is null) yield break;

        foreach (var rel in new[]
                 {
                     @"SB\Saved\Logs\SB.log",
                     @"SB\Saved\Logs\SB-Win64-Shipping.log",
                     @"SB\Saved\Logs\SB-Win64-Shipping-backup-2024.01.01-00.00.00.log",
                     @"SB\Binaries\Win64\UE4SS.log",
                 })
        {
            var path = Path.Combine(gameDir, rel);
            if (File.Exists(path)) yield return path;
        }

        var logsDir = Path.Combine(gameDir, "SB", "Saved", "Logs");
        if (Directory.Exists(logsDir))
        {
            foreach (var log in Directory.EnumerateFiles(logsDir, "*.log").Take(4))
            {
                yield return log;
            }
        }
    }

    private static string? CurrentManagerLog()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogsRoot)) return null;
            return new DirectoryInfo(AppPaths.LogsRoot)
                .EnumerateFiles("*.log")
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void AddFile(ZipArchive zip, string path, string entryName)
    {
        try
        {
            zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
        }
        catch (Exception ex)
        {
            Log.Warn($"导出崩溃文件失败 {path}: {ex.Message}");
        }
    }
}
