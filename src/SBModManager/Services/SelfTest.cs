using System.IO.Compression;
using System.Text;
using System.Text.Json;
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

            // 2a4) CNS base pack without the SB\ prefix (Binaries/ + Content/ at archive root)
            var noPrefixZip = Path.Combine(AppPaths.TempRoot, "selftest-cnsbase.zip");
            if (File.Exists(noPrefixZip)) File.Delete(noPrefixZip);
            using (var zip = ZipFile.Open(noPrefixZip, ZipArchiveMode.Create))
            {
                void AddNoPrefix(string entryName, string content)
                {
                    var entry = zip.CreateEntry(entryName);
                    using var w = new StreamWriter(entry.Open());
                    w.Write(content);
                }

                AddNoPrefix("Binaries/Win64/ue4ss/Mods/DekCNS/enabled.txt", "");
                AddNoPrefix("Content/Paks/LogicMods/DekCNS_P.pak", "pak");
                AddNoPrefix("Content/Paks/~mods/CustomNanosuitSystem/Cosmetics/X.dekcns.json", "{}");
                AddNoPrefix("readme.txt", "merge into game root");
            }

            var noPrefixResult = ModImporter.ImportArchive(noPrefixZip);
            Check("无 SB 前缀的根目录包导入", noPrefixResult.Mod is not null, noPrefixResult.Error);
            if (noPrefixResult.Mod is { } noPrefixMod)
            {
                madeMods.Add(noPrefixMod);
                Check("无 SB 前缀包识别为游戏根目录", noPrefixMod.Type == ModType.GameRoot,
                    noPrefixMod.Type.ToString());
                Check("无 SB 前缀包补齐 SB 路径",
                    noPrefixMod.Files.All(f => f.StartsWith(@"SB\", StringComparison.OrdinalIgnoreCase)),
                    noPrefixMod.Files.FirstOrDefault() ?? "");
                var deploy = Deployer.Deploy(noPrefixMod, gameDir, force: true);
                Check("无 SB 前缀包部署成功", deploy.Success, deploy.Error);
                var cnsJson = Path.Combine(gameDir, "SB", "Content", "Paks", "~mods",
                    "CustomNanosuitSystem", "Cosmetics", "X.dekcns.json");
                var logicPak = Path.Combine(gameDir, "SB", "Content", "Paks", "LogicMods", "DekCNS_P.pak");
                Check("无 SB 前缀包文件落位", File.Exists(cnsJson) && File.Exists(logicPak), cnsJson);
                Deployer.Undeploy(noPrefixMod.Id);
            }

            // 2a5) legacy record without SB\ prefix (Binaries/Content at root) migrates to GameRoot
            var legacyNoPrefix = new ModRecord
            {
                Name = "selftest-legacy-noprefix",
                Type = ModType.Loose,
                Files = new List<string>
                {
                    @"Binaries\Win64\ue4ss\Mods\DekCNS\enabled.txt",
                    @"Content\Paks\LogicMods\DekCNS_P.pak",
                },
            };
            var legacyDir = AppPaths.ModDir(legacyNoPrefix.Id);
            Directory.CreateDirectory(Path.Combine(legacyDir, "Binaries", "Win64", "ue4ss", "Mods", "DekCNS"));
            Directory.CreateDirectory(Path.Combine(legacyDir, "Content", "Paks", "LogicMods"));
            File.WriteAllText(Path.Combine(legacyDir, "Binaries", "Win64", "ue4ss", "Mods", "DekCNS", "enabled.txt"), "");
            File.WriteAllText(Path.Combine(legacyDir, "Content", "Paks", "LogicMods", "DekCNS_P.pak"), "pak");

            ModStore.Save(legacyNoPrefix);
            AppState.Instance.ReloadLibrary();
            ModImporter.MigrateGameRootOverlaysV2();
            AppState.Instance.ReloadLibrary();
            var migratedNoPrefix = AppState.Instance.Library.FirstOrDefault(r => r.Id == legacyNoPrefix.Id);
            var movedFile = Path.Combine(legacyDir, "SB", "Content", "Paks", "LogicMods", "DekCNS_P.pak");
            var oldFile = Path.Combine(legacyDir, "Content", "Paks", "LogicMods", "DekCNS_P.pak");
            Check("无 SB 前缀旧记录迁移",
                migratedNoPrefix is { Type: ModType.GameRoot } &&
                migratedNoPrefix.Files.All(f => f.StartsWith(@"SB\", StringComparison.OrdinalIgnoreCase)),
                migratedNoPrefix?.Type.ToString());
            Check("迁移同步移动库内文件", File.Exists(movedFile) && !File.Exists(oldFile), movedFile);
            if (migratedNoPrefix is not null)
            {
                ModStore.Delete(migratedNoPrefix);
                AppState.Instance.ReloadLibrary();
            }
            try { Directory.Delete(legacyDir, recursive: true); } catch { }

            // 2b) CNS addon detection + special deploy path
            var cnsZip = Path.Combine(AppPaths.TempRoot, "selftest-cns.zip");
            if (File.Exists(cnsZip)) File.Delete(cnsZip);
            using (var zip = ZipFile.Open(cnsZip, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("CustomNanosuitSystem/Animations/TestOutfit.dekcns.json");
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes(
                    "[{\"UniqueFitID\":\"Eve_Off_Duty_CNS_378\",\"DisplayName\":\"Eve Off Duty\"," +
                    "\"Description\":\"Eve wears Lily's Off Duty outfit\",\"FitMeshType\":\"Body\"," +
                    "\"OutfitTypes\":[\"Dress\"],\"OutfitPaths\":[\"/Game/Outfitmods/Eve_Off_Duty/body/x\"]}]"));
            }
            var cnsResult = ModImporter.ImportArchive(cnsZip);
            Check("CNS 模组导入成功", cnsResult.Mod is not null, cnsResult.Error);
            if (cnsResult.Mod is { } cnsMod)
            {
                madeMods.Add(cnsMod);
                Check("CNS 类型识别", cnsMod.Type == ModType.Cns, cnsMod.Type.ToString());
                Check("CNS 服装名解析", cnsMod.CnsFits.Contains("Eve Off Duty"), string.Join(",", cnsMod.CnsFits));
                Check("CNS 角色解析", cnsMod.CnsCharacters.Contains("Eve"), string.Join(",", cnsMod.CnsCharacters));
                Check("CNS 类型解析", cnsMod.CnsOutfitTypes.Contains("Dress"), string.Join(",", cnsMod.CnsOutfitTypes));
                var deploy = Deployer.Deploy(cnsMod, gameDir, force: false);
                Check("CNS 部署成功", deploy.Success, deploy.Error);
                var target = Path.Combine(Deployer.PaksModsDir(gameDir), "CustomNanosuitSystem", "Animations", "TestOutfit.dekcns.json");
                Check("CNS 文件进入 CustomNanosuitSystem", File.Exists(target), target);
                Deployer.Undeploy(cnsMod.Id);
            }

            // 2c) version grouping key
            Check("版本分组 GroupKey", ModImporter.NormalizeGroup("My Cool Mod v1.2.3") == "my cool mod",
                ModImporter.NormalizeGroup("My Cool Mod v1.2.3"));

            // 2c2) BPModLoader pak -> SB\Content\Paks\LogicMods
            var logicZip = Path.Combine(AppPaths.TempRoot, "selftest-logicmod.zip");
            if (File.Exists(logicZip)) File.Delete(logicZip);
            using (var zip = ZipFile.Open(logicZip, ZipArchiveMode.Create))
            {
                void AddLogic(string entryName, string content)
                {
                    var entry = zip.CreateEntry(entryName);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }

                AddLogic("Content/Paks/LogicMods/BPMod_P.pak", "pak");
                AddLogic("Content/Paks/LogicMods/BPMod.ucas", "ucas");
                AddLogic("readme.txt", "BPModLoader pak");
            }

            var logicResult = ModImporter.ImportArchive(logicZip);
            Check("逻辑模组包导入", logicResult.Mod is not null, logicResult.Error);
            if (logicResult.Mod is { } logicMod)
            {
                madeMods.Add(logicMod);
                Check("逻辑模组类型识别", logicMod.Type == ModType.LogicMod, logicMod.Type.ToString());
                Check("逻辑模组路径规范化",
                    logicMod.Files.All(f => f.StartsWith(@"SB\Content\Paks\LogicMods\",
                        StringComparison.OrdinalIgnoreCase)),
                    string.Join(",", logicMod.Files));
                var logicDeploy = Deployer.Deploy(logicMod, gameDir, force: true);
                var logicTarget = Path.Combine(gameDir, "SB", "Content", "Paks", "LogicMods", "BPMod_P.pak");
                Check("逻辑模组部署到 LogicMods", logicDeploy.Success && File.Exists(logicTarget), logicTarget);
                Deployer.Undeploy(logicMod.Id);
                Check("逻辑模组停用后移除", !File.Exists(logicTarget));
            }

            // 2d) home-menu video mods (.bk2 -> SB\Content\Movies)
            var videoZip = Path.Combine(AppPaths.TempRoot, "selftest-video.zip");
            if (File.Exists(videoZip)) File.Delete(videoZip);
            using (var zip = ZipFile.Open(videoZip, ZipArchiveMode.Create))
            {
                void AddVideoEntry(string entryName, string content)
                {
                    var entry = zip.CreateEntry(entryName);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }

                AddVideoEntry("MenuLoop.bk2", "KB2-video");
                AddVideoEntry("readme.txt", "replace the main menu video");
            }

            var videoResult = ModImporter.ImportArchive(videoZip);
            Check("主页视频包导入", videoResult.Mod is not null, videoResult.Error);
            if (videoResult.Mod is { } videoMod)
            {
                madeMods.Add(videoMod);
                Check("主页视频类型识别", videoMod.Type == ModType.Video, videoMod.Type.ToString());
                Check("主页视频路径规范化",
                    videoMod.Files.Count == 1 &&
                    videoMod.Files[0].Equals(@"SB\Content\Movies\MenuLoop.bk2", StringComparison.OrdinalIgnoreCase),
                    string.Join(",", videoMod.Files));
                var deploy = Deployer.Deploy(videoMod, gameDir, force: true);
                var videoTarget = Path.Combine(gameDir, "SB", "Content", "Movies", "MenuLoop.bk2");
                Check("主页视频部署到 Content/Movies", deploy.Success && File.Exists(videoTarget), videoTarget);
                Deployer.Undeploy(videoMod.Id);
                Check("主页视频停用后移除", !File.Exists(videoTarget));
            }

            // 2e) archive already using Content\Movies and the SB\Content\Movies tree
            var videoZip2 = Path.Combine(AppPaths.TempRoot, "selftest-video2.zip");
            if (File.Exists(videoZip2)) File.Delete(videoZip2);
            using (var zip = ZipFile.Open(videoZip2, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("Content/Movies/Intro.bk2");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("KB2-intro");
            }

            var videoResult2 = ModImporter.ImportArchive(videoZip2);
            Check("Content/Movies 视频包识别为视频",
                videoResult2.Mod is { Type: ModType.Video } &&
                videoResult2.Mod.Files.All(f => f.StartsWith(@"SB\Content\Movies\", StringComparison.OrdinalIgnoreCase)),
                videoResult2.Mod?.Type.ToString());
            if (videoResult2.Mod is { } videoMod2)
            {
                madeMods.Add(videoMod2);
                Deployer.Undeploy(videoMod2.Id);
            }

            var videoZip3 = Path.Combine(AppPaths.TempRoot, "selftest-video3.zip");
            if (File.Exists(videoZip3)) File.Delete(videoZip3);
            using (var zip = ZipFile.Open(videoZip3, ZipArchiveMode.Create))
            {
                void AddWrapped(string entryName, string content)
                {
                    var entry = zip.CreateEntry(entryName);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }

                AddWrapped("VideoPack/SB/Content/Movies/Wrapped.bk2", "KB2-wrapped");
                AddWrapped("VideoPack/说明.txt", "merge into game root");
            }

            var videoResult3 = ModImporter.ImportArchive(videoZip3);
            Check("带 SB 前缀的视频包识别", videoResult3.Mod is { Type: ModType.Video },
                videoResult3.Mod?.Type.ToString());
            if (videoResult3.Mod is { } videoMod3)
            {
                madeMods.Add(videoMod3);
                Deployer.Undeploy(videoMod3.Id);
            }

            // 2f) mixed archive must not be classified as video-only
            var mixedZip = Path.Combine(AppPaths.TempRoot, "selftest-video-mixed.zip");
            if (File.Exists(mixedZip)) File.Delete(mixedZip);
            using (var zip = ZipFile.Open(mixedZip, ZipArchiveMode.Create))
            {
                void AddMixed(string entryName, string content)
                {
                    var entry = zip.CreateEntry(entryName);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }

                AddMixed("Menu.bk2", "KB2-video");
                AddMixed("Mixed_P.pak", "pak");
            }

            var mixedResult = ModImporter.ImportArchive(mixedZip);
            Check("混合压缩包不按视频处理",
                mixedResult.Mod is not null && mixedResult.Mod.Type != ModType.Video &&
                mixedResult.Mod.Notes.Contains("混合", StringComparison.Ordinal),
                mixedResult.Mod?.Type.ToString() ?? mixedResult.Error);
            if (mixedResult.Mod is { } mixedMod)
            {
                madeMods.Add(mixedMod);
                Deployer.Undeploy(mixedMod.Id);
            }

            // 2g) mixed pack that still has a game-tree layout merges as game root
            var hybridZip = Path.Combine(AppPaths.TempRoot, "selftest-video-hybrid.zip");
            if (File.Exists(hybridZip)) File.Delete(hybridZip);
            using (var zip = ZipFile.Open(hybridZip, ZipArchiveMode.Create))
            {
                void AddHybrid(string entryName, string content)
                {
                    var entry = zip.CreateEntry(entryName);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }

                AddHybrid("SB/Content/Movies/Hybrid.bk2", "KB2-hybrid");
                AddHybrid("Extra_P.pak", "pak");
            }

            var hybridResult = ModImporter.ImportArchive(hybridZip);
            Check("混合包保留游戏根目录结构",
                hybridResult.Mod is { Type: ModType.GameRoot } &&
                hybridResult.Mod.Files.Any(f => f.EndsWith("Hybrid.bk2", StringComparison.OrdinalIgnoreCase)),
                hybridResult.Mod?.Type.ToString() ?? hybridResult.Error);
            if (hybridResult.Mod is { } hybridMod)
            {
                madeMods.Add(hybridMod);
                Deployer.Undeploy(hybridMod.Id);
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
            var scriptResult = ModImporter.ImportArchive(ue4ssZip);
            Check("UE4SS 脚本模组导入成功", scriptResult.Mod is not null, scriptResult.Error);
            if (scriptResult.Mod is { } scriptMod)
            {
                madeMods.Add(scriptMod);
                Check("UE4SS 类型识别", scriptMod.Type == ModType.Ue4ss, scriptMod.Type.ToString());

                var modsDir = Deployer.Ue4ssModsDir(gameDir);
                Directory.CreateDirectory(modsDir);
                var modsJsonPath = Path.Combine(modsDir, "mods.json");
                File.WriteAllText(modsJsonPath,
                    "[ { \"mod_name\": \"CheatManagerEnablerMod\", \"mod_enabled\": true } ]");

                var deploy = Deployer.Deploy(scriptMod, gameDir, force: false);
                Check("UE4SS 模组部署成功", deploy.Success, deploy.Error);
                var target = Path.Combine(Deployer.Ue4ssModsDir(gameDir), scriptMod.FolderName, "scripts", "main.lua");
                Check("脚本文件出现在 ue4ss/Mods", File.Exists(target), target);
                Check("UE4SS mods.json 记录为启用", ModEnabledInJson(modsJsonPath, scriptMod.FolderName) == true,
                    File.Exists(modsJsonPath) ? File.ReadAllText(modsJsonPath) : "missing");

                Deployer.Undeploy(scriptMod.Id);
                Check("UE4SS mods.json 记录为停用", ModEnabledInJson(modsJsonPath, scriptMod.FolderName) == false,
                    File.Exists(modsJsonPath) ? File.ReadAllText(modsJsonPath) : "missing");
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

            // 6) in-app update helpers
            Check("版本比较", AppUpdater.CompareVersions("1.6.0", "1.5.3") > 0 &&
                             AppUpdater.CompareVersions("1.5.3", "1.5.3") == 0 &&
                             AppUpdater.CompareVersions("1.5.3", "1.6.0") < 0);

            if (Environment.GetEnvironmentVariable("SBMM_TEST_UPDATE") is { Length: > 0 })
            {
                try
                {
                    var update = AppUpdater.CheckAsync().GetAwaiter().GetResult();
                    Check("应用更新检查", !string.IsNullOrWhiteSpace(update.Tag) && update.Package is not null,
                        $"{update.Tag} / {update.Package?.Name ?? "无安装包"} / newer={update.IsNewer}");
                    if (Environment.GetEnvironmentVariable("SBMM_TEST_UPDATE_PREPARE") is { Length: > 0 })
                    {
                        var staged = AppUpdater.PrepareAsync(update).GetAwaiter().GetResult();
                        Check("更新包下载/校验/解压", File.Exists(Path.Combine(staged, "SBModManager.exe")), staged);
                        try
                        {
                            Directory.Delete(Path.GetDirectoryName(staged)!, recursive: true);
                        }
                        catch
                        {
                            // ignore cleanup failure
                        }
                    }
                }
                catch (Exception ex)
                {
                    Check("应用更新检查", false, ex.Message);
                }
            }

            // Optional live GitHub check (SBMM_TEST_GITHUB=owner/repo)
            if (Environment.GetEnvironmentVariable("SBMM_TEST_GITHUB") is { Length: > 0 } repo)
            {
                try
                {
                    var release = GitHubService.GetLatestReleaseAsync(repo).GetAwaiter().GetResult();
                    var asset = GitHubService.PickUe4ssAsset(release);
                    Check("GitHub Release 解析 (" + repo + ")",
                        !string.IsNullOrWhiteSpace(release.TagName) && asset is not null,
                        $"{release.TagName} / {asset?.Name ?? "无 zip"} / {release.Assets.Count} 个资源");
                }
                catch (Exception ex)
                {
                    Check("GitHub Release 解析 (" + repo + ")", false, ex.Message);
                }
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
                    Check("UE4SS 类型为运行库", releaseMod.Type == ModType.Ue4ssRuntime, releaseMod.Type.ToString());
                    var releaseTargets = Deployer.ComputeTargets(releaseMod, gameDir);
                    var proxyTarget = Path.Combine(gameDir, "SB", "Binaries", "Win64", "dwmapi.dll");
                    Check("UE4SS 部署目标包含 dwmapi.dll",
                        releaseTargets.Any(t => string.Equals(t.Target, proxyTarget, StringComparison.OrdinalIgnoreCase)),
                        string.Join("; ", releaseTargets.Take(3).Select(t => t.Target)));
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
    private static bool? ModEnabledInJson(string path, string modName)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("mod_name", out var name) &&
                    string.Equals(name.GetString(), modName, StringComparison.OrdinalIgnoreCase) &&
                    entry.TryGetProperty("mod_enabled", out var enabled))
                {
                    return enabled.ValueKind == JsonValueKind.True;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

}
