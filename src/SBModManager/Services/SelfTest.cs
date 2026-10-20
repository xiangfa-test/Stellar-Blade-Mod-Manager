using System.IO.Compression;
using System.Text;
using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>Non-interactive checks for the import / deploy pipeline (--selftest).</summary>
public static class SelfTest
{
    public static void Run(string reportPath)
    {
        var report = new StringBuilder();
        var failures = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            if (!ok) failures++;
            report.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : " — " + detail)}");
        }

        var state = AppState.Instance;
        var gameDir = state.GameDir;
        report.AppendLine($"gameDir={gameDir ?? "(null)"}");
        report.AppendLine($"library={AppPaths.ModsRoot}");
        Check("游戏目录有效", gameDir is not null, gameDir);

        if (gameDir is null)
        {
            File.WriteAllText(reportPath, report.ToString());
            return;
        }

        var madeMods = new List<ModRecord>();
        try
        {
            // 1) PAK mod import + deploy
            var pakZip = Path.Combine(AppPaths.TempRoot, "selftest-pak.zip");
            if (File.Exists(pakZip)) File.Delete(pakZip);
            using (var zip = ZipFile.Open(pakZip, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("TestPakMod_P.pak");
                using var s = entry.Open();
                s.Write(new byte[1024]);
            }
            var pakResult = ModImporter.ImportArchive(pakZip);
            Check("PAK 导入成功", pakResult.Mod is not null, pakResult.Error);
            if (pakResult.Mod is { } pakMod)
            {
                madeMods.Add(pakMod);
                Check("PAK 类型识别", pakMod.Type == ModType.Pak, pakMod.Type.ToString());
                var deploy = Deployer.Deploy(pakMod, gameDir, force: false);
                Check("PAK 部署成功", deploy.Success, deploy.Error ?? (deploy.Conflicts.Count > 0 ? "conflict" : null));
                var target = Path.Combine(Deployer.PaksModsDir(gameDir), "TestPakMod_P.pak");
                Check("PAK 文件出现在 ~mods", File.Exists(target), target);
                Deployer.Undeploy(pakMod.Id);
                Check("PAK 停用后文件被移除", !File.Exists(target));
            }

            // 2) Loose mod import + deploy
            var looseZip = Path.Combine(AppPaths.TempRoot, "selftest-loose.zip");
            if (File.Exists(looseZip)) File.Delete(looseZip);
            using (var zip = ZipFile.Open(looseZip, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("LooseTest/Animations/test.json");
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes("{}"));
            }
            var looseResult = ModImporter.ImportArchive(looseZip);
            Check("资源模组导入成功", looseResult.Mod is not null, looseResult.Error);
            if (looseResult.Mod is { } looseMod)
            {
                madeMods.Add(looseMod);
                Check("资源模组类型识别", looseMod.Type == ModType.Loose, looseMod.Type.ToString());
                var deploy = Deployer.Deploy(looseMod, gameDir, force: false);
                Check("资源模组部署成功", deploy.Success, deploy.Error);
                var target = Path.Combine(Deployer.PaksModsDir(gameDir), looseMod.FolderName, "Animations", "test.json");
                Check("资源文件出现在 ~mods/<名称>", File.Exists(target), target);
                Deployer.Undeploy(looseMod.Id);
                Check("资源模组停用后文件被移除", !File.Exists(target));
            }

            // 3) UE4SS script mod detection
            var ue4ssZip = Path.Combine(AppPaths.TempRoot, "selftest-ue4ss.zip");
            if (File.Exists(ue4ssZip)) File.Delete(ue4ssZip);
            using (var zip = ZipFile.Open(ue4ssZip, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("MyScriptMod/scripts/main.lua");
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes("-- test"));
            }
            var ue4ssResult = ModImporter.ImportArchive(ue4ssZip);
            Check("UE4SS 脚本模组导入成功", ue4ssResult.Mod is not null, ue4ssResult.Error);
            if (ue4ssResult.Mod is { } ue4ssMod)
            {
                madeMods.Add(ue4ssMod);
                Check("UE4SS 类型识别", ue4ssMod.Type == ModType.Ue4ss, ue4ssMod.Type.ToString());
                var deploy = Deployer.Deploy(ue4ssMod, gameDir, force: false);
                Check("UE4SS 模组部署成功", deploy.Success, deploy.Error);
                var target = Path.Combine(Deployer.Ue4ssModsDir(gameDir), ue4ssMod.FolderName, "scripts", "main.lua");
                Check("脚本文件出现在 ue4ss/Mods", File.Exists(target), target);
                Deployer.Undeploy(ue4ssMod.Id);
            }

            // 4) External scan should not throw
            var external = Deployer.ScanExternal(gameDir);
            Check("外部文件扫描正常", true, $"{external.Count} 组");

            // 5) Deployed state should be empty after cleanup
            Check("部署状态已清空", state.Deployed.Mods.Count == 0,
                string.Join(",", state.Deployed.Mods.Keys));
        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine("[FAIL] 未预期异常: " + ex);
        }
        finally
        {
            foreach (var mod in madeMods)
            {
                try
                {
                    Deployer.Undeploy(mod.Id);
                    ModStore.Delete(mod);
                }
                catch (Exception ex)
                {
                    report.AppendLine("[WARN] 清理失败: " + ex.Message);
                }
            }
            report.AppendLine($"RESULT: {(failures == 0 ? "ALL PASS" : $"{failures} FAILED")}");
            File.WriteAllText(reportPath, report.ToString());
            Log.Info("自检结束: " + (failures == 0 ? "全部通过" : failures + " 项失败"));
        }
    }
}
