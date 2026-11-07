namespace SBModManager.Services;

/// <summary>What the compatibility check found for the installed UE4SS.</summary>
public enum Ue4ssCompatState
{
    /// <summary>No game folder configured yet.</summary>
    NoGameDir,

    /// <summary>UE4SS is not installed, so there is nothing to fix.</summary>
    NotInstalled,

    /// <summary>No known-broken signature file present; nothing to do.</summary>
    Healthy,

    /// <summary>A signature that breaks launching after a game update is still active.</summary>
    NeedsFix,

    /// <summary>The workaround has already been applied.</summary>
    Fixed,
}

/// <summary>Result of <see cref="Ue4ssCompatFix.Inspect"/>.</summary>
public sealed record Ue4ssCompatStatus(
    Ue4ssCompatState State,
    string SignatureDir,
    string? PendingSignature,
    string? AppliedSignature,
    string? LogTail)
{
    public bool CanApply => State == Ue4ssCompatState.NeedsFix;

    public bool CanRevert => State == Ue4ssCompatState.Fixed;
}

/// <summary>
/// Applies the known-good workaround for "the game exits a second after launch" after a Stellar
/// Blade update: UE4SS scans for the FText constructor by AOB signature, the new build moves it,
/// the scan aborts and the process disappears without a crash dialog or WER report.
/// The upstream recommendation (Chrisr0/RE-UE4SS release notes) is to rename the stale signature
/// file so UE4SS stops trying to override it — this service does exactly that, reversibly.
/// </summary>
public static class Ue4ssCompatFix
{
    /// <summary>Signature files known to break launching on newer game builds.</summary>
    private static readonly string[] KnownBrokenSignatures = { "FText_Constructor.lua" };

    /// <summary>Prefix used by UE4SS to disable a signature file: <c>FText_Constructor.lua</c> → <c>dFText_Constructor.lua</c>.</summary>
    private const string DisabledPrefix = "d";

    /// <summary>Keep the original next to the renamed file so the change is easy to undo by hand.</summary>
    private static string BackupPathFor(string dir, string signature) =>
        Path.Combine(dir, signature + ".disabled-" + DateTime.Now.ToString("yyyyMMdd"));

    public static string SignatureDir(string gameDir) =>
        Path.Combine(Deployer.BinariesDir(gameDir), "ue4ss", "UE4SS_Signatures");

    /// <summary>Inspect the installed UE4SS without changing anything.</summary>
    public static Ue4ssCompatStatus Inspect(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            return new Ue4ssCompatStatus(Ue4ssCompatState.NoGameDir, "", null, null, null);
        }

        var dir = SignatureDir(gameDir);
        var logTail = LastLogLine(gameDir);

        if (!Directory.Exists(dir) || !Deployer.IsUe4ssInstalled(gameDir))
        {
            return new Ue4ssCompatStatus(Ue4ssCompatState.NotInstalled, dir, null, null, logTail);
        }

        foreach (var signature in KnownBrokenSignatures)
        {
            if (File.Exists(Path.Combine(dir, signature)))
            {
                return new Ue4ssCompatStatus(Ue4ssCompatState.NeedsFix, dir, signature, null, logTail);
            }
        }

        foreach (var signature in KnownBrokenSignatures)
        {
            if (File.Exists(Path.Combine(dir, DisabledPrefix + signature)))
            {
                return new Ue4ssCompatStatus(
                    Ue4ssCompatState.Fixed, dir, null, DisabledPrefix + signature, logTail);
            }
        }

        return new Ue4ssCompatStatus(Ue4ssCompatState.Healthy, dir, null, null, logTail);
    }

    /// <summary>
    /// Rename every stale signature so UE4SS stops overriding it. Returns the number of files
    /// changed; <paramref name="message"/> explains what happened for the UI and the log.
    /// </summary>
    public static int Apply(string? gameDir, out string message)
    {
        var status = Inspect(gameDir);
        if (status.State != Ue4ssCompatState.NeedsFix || status.PendingSignature is null)
        {
            message = Localization.T("当前没有需要修复的 UE4SS 签名。");
            return 0;
        }

        var changed = 0;
        var details = new List<string>();
        foreach (var signature in KnownBrokenSignatures)
        {
            var source = Path.Combine(status.SignatureDir, signature);
            if (!File.Exists(source)) continue;

            var disabled = Path.Combine(status.SignatureDir, DisabledPrefix + signature);
            try
            {
                // Keep a copy first: if anything below fails the original must still exist.
                var backup = BackupPathFor(status.SignatureDir, signature);
                try
                {
                    File.Copy(source, backup, overwrite: true);
                }
                catch (Exception copyEx)
                {
                    Log.Warn("备份 UE4SS 签名失败（继续尝试重命名）: " + copyEx.Message);
                }

                File.Move(source, disabled, overwrite: true);
                changed++;
                details.Add($"{signature} → {DisabledPrefix}{signature}");
                Log.Info($"已禁用失效的 UE4SS 签名: {source} → {disabled}");
            }
            catch (Exception ex)
            {
                Log.Error($"禁用 UE4SS 签名失败 {source}", ex);
                message = Localization.T("改名失败：") + ex.Message;
                return changed;
            }
        }

        message = changed == 0
            ? Localization.T("当前没有需要修复的 UE4SS 签名。")
            : string.Format(Localization.T("已应用修复：{0}"), string.Join("，", details));
        return changed;
    }

    /// <summary>Undo <see cref="Apply"/>; used when the workaround makes things worse.</summary>
    public static int Revert(string? gameDir, out string message)
    {
        var status = Inspect(gameDir);
        if (status.State != Ue4ssCompatState.Fixed)
        {
            message = Localization.T("当前没有已应用的修复。");
            return 0;
        }

        var changed = 0;
        foreach (var signature in KnownBrokenSignatures)
        {
            var disabled = Path.Combine(status.SignatureDir, DisabledPrefix + signature);
            if (!File.Exists(disabled)) continue;

            var original = Path.Combine(status.SignatureDir, signature);
            try
            {
                File.Move(disabled, original, overwrite: true);
                changed++;
                Log.Info($"已恢复 UE4SS 签名: {disabled} → {original}");
            }
            catch (Exception ex)
            {
                Log.Error($"恢复 UE4SS 签名失败 {disabled}", ex);
                message = Localization.T("恢复失败：") + ex.Message;
                return changed;
            }
        }

        message = changed == 0
            ? Localization.T("当前没有已应用的修复。")
            : Localization.T("已撤销修复，签名文件已恢复原名。");
        return changed;
    }

    /// <summary>Last non-empty line of the UE4SS log — it names the real reason an injection died.</summary>
    public static string? LastLogLine(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return null;

        var path = Path.Combine(Deployer.BinariesDir(gameDir), "ue4ss", "UE4SS.log");
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
            // locked or mid-write: the caller still gets a useful status without the tail
        }
        return null;
    }
}
