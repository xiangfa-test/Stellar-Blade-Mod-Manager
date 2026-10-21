using System.IO.Compression;
using System.Text;
using SBModManager.Models;
using SharpCompress.Common;
using SharpCompress.Writers;

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

            // Optional live collection fixture (needs network; enabled with SBMM_TEST_COLLECTION).
            if (Environment.GetEnvironmentVariable("SBMM_TEST_COLLECTION") is { Length: > 0 } collectionSlug)
            {
                try
                {
                    var collection = NexusService.GetCollectionAsync(collectionSlug)
                        .GetAwaiter().GetResult();
                    Check("合集解析 (" + collectionSlug + ")",
                        collection.ModCount > 0 && collection.Mods.Count > 0,
                        $"{collection.Name} rev={collection.RevisionNumber} mods={collection.Mods.Count}");
                }
                catch (Exception ex)
                {
                    Check("合集解析 (" + collectionSlug + ")", false, ex.Message);
                }
            }

            // Optional extra archive fixture (used for testing real 7-Zip output).
            if (Environment.GetEnvironmentVariable("SBMM_TEST_ARCHIVE") is { Length: > 0 } extraArchive &&
                File.Exists(extraArchive))
            {
                var extraResult = ModImporter.ImportArchive(extraArchive);
                Check("外部压缩包解压 (" + Path.GetFileName(extraArchive) + ")", extraResult.Mod is not null,
                    extraResult.Error);
                if (extraResult.Mod is { } extraMod)
                {
                    madeMods.Add(extraMod);
                    Deployer.Undeploy(extraMod.Id);
                }
            }

            // 2a) built-in 7z engine (no external 7-Zip installation)
            var sevenSource = Path.Combine(AppPaths.TempRoot, "selftest-seven-src.pak");
            var sevenArchive = Path.Combine(AppPaths.TempRoot, "selftest-seven.7z");
            File.WriteAllBytes(sevenSource, new byte[128]);
            if (File.Exists(sevenArchive)) File.Delete(sevenArchive);
            using (var fs = File.Create(sevenArchive))
            using (var writer = WriterFactory.OpenWriter(fs, ArchiveType.SevenZip,
                       new WriterOptions(CompressionType.LZMA)))
            using (var src = File.OpenRead(sevenSource))
            {
                writer.Write("SevenTest_P.pak", src, null);
            }
            var sevenResult = ModImporter.ImportArchive(sevenArchive);
            Check("内置 7z 解压导入", sevenResult.Mod is not null, sevenResult.Error);
            if (sevenResult.Mod is { } sevenMod)
            {
                madeMods.Add(sevenMod);
                Check("7z 模组类型识别", sevenMod.Type == ModType.Pak, sevenMod.Type.ToString());
                var deploy = Deployer.Deploy(sevenMod, gameDir, force: false);
                Check("7z 模组部署成功", deploy.Success, deploy.Error);
                Deployer.Undeploy(sevenMod.Id);
            }

            // 2a2) SB/ overlay pack (Custom Nanosuit System style: merge into the game root)
            var overlayZip = Path.Combine(AppPaths.TempRoot, "selftest-overlay.zip");
            if (File.Exists(overlayZip)) File.Delete(overlayZip);
            using (var zip = ZipFile.Open(overlayZip, ZipArchiveMode.Create))
            {
                void Add(string entryName, string content)
                {
                    var entry = zip.CreateEntry(entryName);
                    using var w = new StreamWriter(entry.Open());
                    w.Write(content);
                }

                Add("CNSPack-v1/readme.txt", "install by merging the SB folder");
                Add("CNSPack-v1/SB/Content/Paks/~mods/CustomNanosuitSystem/Animations/Overlay.dekcns.json", "{}");
                Add("CNSPack-v1/SB/Binaries/Win64/ue4ss/Mods/CNS/scripts/main.lua", "return {}");
            }

            var overlayResult = ModImporter.ImportArchive(overlayZip);
            Check("SB 根目录包导入成功", overlayResult.Mod is not null, overlayResult.Error);
            if (overlayResult.Mod is { } overlayMod)
            {
                madeMods.Add(overlayMod);
                Check("SB 根目录包类型识别", overlayMod.Type == ModType.GameRoot, overlayMod.Type.ToString());
                var deploy = Deployer.Deploy(overlayMod, gameDir, force: false);
                Check("SB 根目录包部署成功", deploy.Success, deploy.Error);
                var overlayA = Path.Combine(gameDir, "SB", "Content", "Paks", "~mods",
                    "CustomNanosuitSystem", "Animations", "Overlay.dekcns.json");
                var overlayB = Path.Combine(gameDir, "SB", "Binaries", "Win64",
                    "ue4ss", "Mods", "CNS", "scripts", "main.lua");
                Check("SB 根目录文件合并到游戏根目录", File.Exists(overlayA) && File.Exists(overlayB),
                    overlayA + " | " + overlayB);
                Deployer.Undeploy(overlayMod.Id);
                Check("SB 根目录包停用清理", !File.Exists(overlayA));
            }

            // 2a3) legacy CNS pack records (stripped SB\ prefix) get migrated to GameRoot
            var legacy = new ModRecord
            {
                Name = "selftest-legacy-cns",
                Type = ModType.Pak,
                Files = new List<string>
                {
                    @"Content\Paks\~mods\CustomNanosuitSystem\Animations\Legacy.dekcns.json",
                },
            };
            ModStore.Save(legacy);
            AppState.Instance.ReloadLibrary();
            ModImporter.MigrateGameRootOverlays();
            AppState.Instance.ReloadLibrary();
            var migratedRecord = AppState.Instance.Library.FirstOrDefault(r => r.Id == legacy.Id);
            Check("旧 CNS 包迁移为游戏根目录",
                migratedRecord is { Type: ModType.GameRoot } &&
                migratedRecord.Files.All(f => f.StartsWith(@"SB\", StringComparison.OrdinalIgnoreCase)),
                migratedRecord?.Type.ToString());
            if (migratedRecord is not null)
            {
                ModStore.Delete(migratedRecord);
                AppState.Instance.ReloadLibrary();
            }

            // 2b) CNS addon detection + special deploy path
            var cnsZip = Path.Combine(AppPaths.TempRoot, "selftest-cns.zip");
            if (File.Exists(cnsZip)) File.Delete(cnsZip);
            using (var zip = ZipFile.Open(cnsZip, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("CustomNanosuitSystem/Animations/TestOutfit.dekcns.json");
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes("{}"));
            }
            var cnsResult = ModImporter.ImportArchive(cnsZip);
            Check("CNS 模组导入成功", cnsResult.Mod is not null, cnsResult.Error);
            if (cnsResult.Mod is { } cnsMod)
            {
                madeMods.Add(cnsMod);
                Check("CNS 类型识别", cnsMod.Type == ModType.Cns, cnsMod.Type.ToString());
                var deploy = Deployer.Deploy(cnsMod, gameDir, force: false);
                Check("CNS 部署成功", deploy.Success, deploy.Error);
                var target = Path.Combine(Deployer.PaksModsDir(gameDir), "CustomNanosuitSystem", "Animations", "TestOutfit.dekcns.json");
                Check("CNS 文件进入 CustomNanosuitSystem", File.Exists(target), target);
                Deployer.Undeploy(cnsMod.Id);
            }

            // 2c) version grouping key
            Check("版本分组 GroupKey", ModImporter.NormalizeGroup("My Cool Mod v1.2.3") == "my cool mod",
                ModImporter.NormalizeGroup("My Cool Mod v1.2.3"));

            // 3) UE4SS script mod detection
            var ue4ssZip = Path.Combine(AppPaths.TempRoot, "selftest-ue4ss.zip");
            if (File.Exists(ue4ssZip)) File.Delete(ue4ssZip);
            using (var zip = ZipFile.Open(ue4ssZip, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("MyScriptMod/scripts/main.lua");
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes("-- test"));
            }
            var scriptResult = ModImporter.ImportArchive(ue4ssZip);
            Check("UE4SS 脚本模组导入成功", scriptResult.Mod is not null, scriptResult.Error);
            if (scriptResult.Mod is { } scriptMod)
            {
                madeMods.Add(scriptMod);
                Check("UE4SS 类型识别", scriptMod.Type == ModType.Ue4ss, scriptMod.Type.ToString());
                var deploy = Deployer.Deploy(scriptMod, gameDir, force: false);
                Check("UE4SS 模组部署成功", deploy.Success, deploy.Error);
                var target = Path.Combine(Deployer.Ue4ssModsDir(gameDir), scriptMod.FolderName, "scripts", "main.lua");
                Check("脚本文件出现在 ue4ss/Mods", File.Exists(target), target);
                Deployer.Undeploy(scriptMod.Id);
            }

            // 3b) nxm:// parsing
            var nxmOk = NxmHandler.TryParse("nxm://stellarblade/mods/1234/files/5678?key=abc&expires=99", out var nxmLink)
                        && nxmLink.Domain == "stellarblade" && nxmLink.ModId == 1234 && nxmLink.FileId == 5678;
            Check("nxm 链接解析", nxmOk);
            var linkUrl = NexusService.BuildDownloadLinkUrl("stellarblade", 1234, 5678, "abc", "99");
            var collectionNxm = NxmHandler.TryParse("nxm://stellarblade/collections/n7zgom/revisions/3", out var colLink)
                                 && colLink.IsCollection && colLink.CollectionSlug == "n7zgom";
            Check("nxm 合集链接解析", collectionNxm);
            Check("合集标签生成", CollectionInstaller.TagFor(new NexusCollection { Name = "Demo" }) == "合集:Demo");

            var localRecord = new ModRecord { Name = "selftest-local-tag" };
            CollectionInstaller.TagAsLocal(localRecord, persist: false);
            Check("本地安装默认合集标签", localRecord.Tags.Contains(CollectionInstaller.LocalTag));

            var foreignRecord = new ModRecord { Name = "selftest-foreign-tag", Tags = { "合集:别人的合集" } };
            CollectionInstaller.TagAsLocal(foreignRecord, persist: false);
            Check("已有合集标签不改成本地", !foreignRecord.Tags.Contains(CollectionInstaller.LocalTag));

            var deactivateEmpty = CollectionInstaller.Deactivate("合集:不存在的合集");
            Check("停用不存在的合集不报错", deactivateEmpty.Disabled == 0);

            Check("nxm 免会员直链参数", linkUrl.Contains("key=abc") && linkUrl.Contains("expires=99"), linkUrl);

            // 3c) conflict chain + high-conflict detection (two mods writing the same pak)
            var dupA = Path.Combine(AppPaths.TempRoot, "selftest-dup-a.zip");
            var dupB = Path.Combine(AppPaths.TempRoot, "selftest-dup-b.zip");
            foreach (var f in new[] { dupA, dupB }) { if (File.Exists(f)) File.Delete(f); }
            using (var zip = ZipFile.Open(dupA, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("DupShared_P.pak");
                using var s = e.Open();
                s.Write(new byte[64]);
            }
            using (var zip = ZipFile.Open(dupB, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("DupShared_P.pak");
                using var s = e.Open();
                s.Write(new byte[64]);
            }
            var modA = ModImporter.ImportArchive(dupA).Mod;
            var modB = ModImporter.ImportArchive(dupB).Mod;
            if (modA is not null && modB is not null)
            {
                AppState.Instance.ReloadLibrary();
                madeMods.Add(modA);
                madeMods.Add(modB);
                var deployA = Deployer.Deploy(modA, gameDir, force: false);
                var deployB = Deployer.Deploy(modB, gameDir, force: true);
                Check("冲突模组可强制覆盖", deployA.Success && deployB.Success);
                var conflictReport = Deployer.BuildConflictReport(gameDir);
                Check("冲突链检测", conflictReport.Chains.Any(c => c.Mods.Contains(modA.Name) && c.Mods.Contains(modB.Name)),
                    $"{conflictReport.Chains.Count} 条链");
                Check("高冲突模组统计", conflictReport.HighConflictMods.Count >= 2);
                Deployer.Undeploy(modA.Id);
                Deployer.Undeploy(modB.Id);
            }

            // Optional UE4SS release fixture (SBMM_TEST_UE4SS_ZIP=/path/to/UE4SS.zip)
            if (Environment.GetEnvironmentVariable("SBMM_TEST_UE4SS_ZIP") is { Length: > 0 } ue4ssFixture &&
                File.Exists(ue4ssFixture))
            {
                var releaseResult = ModImporter.ImportArchive(ue4ssFixture);
                Check("UE4SS 包导入", releaseResult.Mod is not null, releaseResult.Error);
                if (releaseResult.Mod is { } releaseMod)
                {
                    madeMods.Add(releaseMod);
                    var files = releaseMod.Files.Select(f => f.Replace('/', '\\')).ToList();
                    Check("UE4SS 包含 dwmapi.dll",
                        files.Any(f => Path.GetFileName(f).Equals("dwmapi.dll", StringComparison.OrdinalIgnoreCase)));
                    Check("UE4SS 包含 ue4ss 目录",
                        files.Any(f => f.Contains(@"ue4ss\", StringComparison.OrdinalIgnoreCase)));
                    var deploy = Deployer.Deploy(releaseMod, gameDir, force: true);
                    Check("UE4SS 部署到 Win64", deploy.Success && Deployer.IsUe4ssInstalled(gameDir), deploy.Error);
                    Deployer.Undeploy(releaseMod.Id);
                    Check("UE4SS 停用后移除", !Deployer.IsUe4ssInstalled(gameDir));
                }
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
