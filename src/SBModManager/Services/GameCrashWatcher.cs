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

    /// <summary>Set when the game vanished; late crash evidence is still collected for a while.</summary>
    private static DateTimeOffset? PendingSince { get; set; }

    public static event Action<CrashReport>? CrashDetected;

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
        try
        {
            if (!AppState.Instance.Config.CaptureGameCrashes) return;

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
                PendingSince = started;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("崩溃监控异常: " + ex.Message);
        }
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
