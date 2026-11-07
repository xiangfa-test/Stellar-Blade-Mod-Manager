using System.Diagnostics;
using System.Text;
using SBModManager.Models;

namespace SBModManager.Services;

public sealed class CrashReport
{
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.Now;
    public string Summary { get; init; } = "";
    public string Analysis { get; init; } = "";
    public string ExceptionCode { get; init; } = "";
    public string FaultModule { get; init; } = "";
    public string? DumpPath { get; init; }
    public string? WerReportPath { get; init; }
    public string LogTail { get; init; } = "";
    public string ReportPath { get; init; } = "";
}

/// <summary>
/// Watches the game process while the manager is running. When the game exits with crash
/// evidence (WER report, crash dump, UE fatal log) it builds a short analysis report.
/// </summary>
public static class GameCrashWatcher
{
    private static readonly object Sync = new();
    private static System.Threading.Timer? _timer;
    private static Process? _game;
    private static DateTimeOffset? _startedAt;
    private static bool _enabled;

    /// <summary>1 while a Poll is running; guards against overlapping timer callbacks.</summary>
    private static int _polling;

    /// <summary>Set when the game vanished; late crash evidence is still collected for a while.</summary>
    private static DateTimeOffset? PendingSince { get; set; }

    public static event Action<CrashReport>? CrashDetected;

    /// <summary>
    /// Raised when the game process vanished within seconds of starting and Windows produced no
    /// crash evidence — the signature of an injection failure (UE4SS built for another game
    /// version), which used to look exactly like "nothing happened" in the logs.
    /// </summary>
    public static event Action<string>? StartupFailureSuspected;

    public static void Start()
    {
        lock (Sync)
        {
            if (_timer is not null) return;
            _enabled = true;
            _timer = new System.Threading.Timer(_ => Poll(), null, 2000, 1000);
            Log.Info("游戏崩溃监控已启动");
        }
    }

    public static void Stop()
    {
        lock (Sync)
        {
            _enabled = false;
            _timer?.Dispose();
            _timer = null;
            _game?.Dispose();
            _game = null;
            _startedAt = null;
        }
    }

    private static void Poll()
    {
        if (!_enabled) return;

        // A single poll can outlive the 1 s timer period (WER scan + dump scan + log tail),
        // which used to let two callbacks run concurrently and race on _game / PendingSince,
        // reporting the same crash twice. The first caller wins; the overlapping tick bails out.
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;

        try
        {
            if (!AppServices.State.Config.CaptureGameCrashes) return;

            var running = FindGameProcess();
            if (running is not null)
            {
                lock (Sync)
                {
                    if (_game is null)
                    {
                        _game = running;
                        _startedAt = DateTimeOffset.Now;
                        Log.Info("检测到游戏已启动，开始监控: " + running.ProcessName);
                    }
                    else
                    {
                        running.Dispose();
                    }
                }
                return;
            }

            Process? exited;
            DateTimeOffset? started;
            lock (Sync)
            {
                exited = _game;
                started = _startedAt;
                _game = null;
                _startedAt = null;
            }

            if (exited is null)
            {
                // WER reports and dumps can land a few seconds after the process dies.
                var pending = PendingSince;
                if (pending is null) return;
                if (DateTimeOffset.Now - pending.Value > TimeSpan.FromSeconds(60))
                {
                    PendingSince = null;
                    return;
                }

                var late = CrashAnalyzer.Build(pending.Value, null);
                if (late is not null)
                {
                    PendingSince = null;
                    Log.Warn($"检测到游戏崩溃（延迟证据）: {late.Summary}");
                    CrashDetected?.Invoke(late);
                }
                return;
            }

            int? exitCode = null;
            try
            {
                if (exited.HasExited) exitCode = exited.ExitCode;
            }
            catch
            {
                // exit code is unavailable for processes we did not start
            }
            exited.Dispose();

            var report = CrashAnalyzer.Build(started ?? DateTimeOffset.Now.AddMinutes(-5), exitCode);
            if (report is not null)
            {
                PendingSince = null;
                Log.Warn($"检测到游戏崩溃: {report.Summary}");
                CrashDetected?.Invoke(report);
            }
            else if (started is not null)
            {
                // The game ran and exited without evidence yet: keep watching briefly for WER
                // reports/dumps that Windows writes a few seconds later.
                var ran = DateTimeOffset.Now - started.Value;
                if (ran < TimeSpan.FromSeconds(30))
                {
                    // "Exited in seconds with no WER report and no dump" is the signature of an
                    // injection failure (UE4SS built for another game version aborts during its
                    // signature scan and the process just disappears), not of a normal crash.
                    var diagnosis = DescribeSilentExit(ran, exitCode);
                    Log.Warn("疑似启动失败: " + diagnosis);
                    try
                    {
                        StartupFailureSuspected?.Invoke(diagnosis);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("启动失败通知处理异常: " + ex.Message);
                    }
                }
                PendingSince = started;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("崩溃监控异常: " + ex.Message);
        }
        finally
        {
            // Release the guard on every exit path (including the early returns above),
            // otherwise one unexpected exception would permanently stop crash detection.
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <summary>
    /// Explain a "the process was there, then it was gone" exit: no WER report and no dump means
    /// nothing crashed from Windows' point of view, so the question is what aborted it. UE4SS is
    /// the usual answer (its signature scan fails after a game update), and its log says why.
    /// </summary>
    private static string DescribeSilentExit(TimeSpan ran, int? exitCode)
    {
        var sb = new StringBuilder();
        sb.Append($"游戏进程 {ran.TotalSeconds:F0} 秒后退出，Windows 没有生成崩溃报告或转储");
        sb.Append($"（退出码 {(exitCode is null ? "未知" : exitCode.Value.ToString())}）。");
        sb.Append("这种「秒退且无 WER」通常是注入失败：UE4SS 与游戏新版本不匹配时，");
        sb.Append("会在 Lua 签名扫描阶段直接退出，进程来不及显示任何窗口。");

        var ue4ssTail = ReadLastLineOfUe4ssLog();
        if (ue4ssTail is not null)
        {
            sb.Append(" UE4SS 日志最后一行：").Append(ue4ssTail);
        }
        else
        {
            sb.Append(" 没有找到 UE4SS 日志（可能未安装 UE4SS）。");
        }

        sb.Append(" 排查：1) 更新 UE4SS 到匹配游戏版本的构建；2) 把 ue4ss\\UE4SS_Signatures 里");
        sb.Append("失效的签名文件改名（上游 Chrisr0/RE-UE4SS 发布说明有对应方法）；");
        sb.Append("3) 临时移除 dwmapi.dll 确认是否为注入问题。");
        return sb.ToString();
    }

    /// <summary>Last non-empty line of the UE4SS log, which carries the real reason for an abort.</summary>
    private static string? ReadLastLineOfUe4ssLog()
    {
        var gameDir = AppServices.State.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir)) return null;

        var path = Path.Combine(gameDir, "SB", "Binaries", "Win64", "ue4ss", "UE4SS.log");
        try
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i])) return lines[i].Trim();
            }
        }
        catch
        {
            // the log may be locked or mid-write; the diagnostic is still useful without it
        }
        return null;
    }

    private static Process? FindGameProcess()
    {
        foreach (var name in new[] { "SB-Win64-Shipping", "SB" })
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length == 0) continue;
                var first = processes[0];
                foreach (var extra in processes.Skip(1)) extra.Dispose();
                return first;
            }
            catch
            {
                // access denied or process vanished
            }
        }
        return null;
    }
}
