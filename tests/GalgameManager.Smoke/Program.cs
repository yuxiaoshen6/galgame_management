using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GalgameManager.Application;
using GalgameManager.Infrastructure;
using Microsoft.Data.Sqlite;

if (args is ["--probe", var reportPath, .. var launchArguments])
{
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new Probe(Environment.CurrentDirectory, launchArguments, Environment.ProcessId)));
    return;
}

Console.OutputEncoding = Encoding.UTF8;
if (args is ["--fixture", var destination])
{
    CreateFixture(Path.GetFullPath(destination));
    return;
}

var temporary = Path.Combine(Path.GetTempPath(), $"GalgameManager-Smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporary);
try
{
    await RunAsync(temporary, args.Contains("--benchmark"));
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(temporary, recursive: true);
}

static async Task RunAsync(string home, bool runBenchmark)
{
    var root = Path.Combine(home, "中文 日本語 Games");
    CreateFixture(root);
    var night = Path.Combine(root, "01 星降る夜");
    var moon = Path.Combine(root, "02 月影 Lantern");
    var sunshine = Path.Combine(root, "04 Lucid Dream");
    const string localSummary = "本地简介第一段。\n\n第二段。";
    var localCover = Path.Combine(night, "cover.png");
    var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jPxoAAAAASUVORK5CYII=");
    File.WriteAllBytes(localCover, imageBytes);
    File.WriteAllText(Path.Combine(night, "metadata.json"), JsonSerializer.Serialize(new { summary = localSummary }));
    File.WriteAllText(Path.Combine(moon, "description.txt"), "本地文本简介。");
    File.WriteAllText(Path.Combine(sunshine, "metadata.json"), "{ invalid");
    var nested = Path.Combine(moon, "bin");
    var removed = Path.Combine(home, "已断开的目录");
    var data = Path.Combine(home, "store");
    var db = Path.Combine(data, "library.db");
    var log = new DiagnosticLog(data);
    var store = new SqliteLibrary(db, log);
    var scanner = new FileScanner(log);
    var files = new MetadataFiles(data);
    var service = new LibraryService(store, store, scanner, new WindowsGameLauncher(log), files);
    store.Initialize();
    store.Initialize();
    await service.AddRootsAsync([root, nested, root.ToUpperInvariant()]);
    var roots = (await service.ListRootsAsync()).ToArray();
    Check(roots.Length == 2, "重复扫描目录必须归为一条");

    var outside = Path.Combine(home, "Outside");
    Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "Outside.exe"), "");
    var junction = Path.Combine(root, "linked");
    using (var createLink = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{junction}\" \"{outside}\"")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    }))
    {
        createLink!.WaitForExit();
        Check(createLink.ExitCode == 0, "样本目录联接必须创建成功");
    }
    var visited = new SynchronousProgress<ScanProgress>(_ => { });
    try
    {
        var preview = await service.ScanRootsAsync([.. roots, new ScanRoot(-1, removed)], visited, CancellationToken.None);
        Check(preview.Games.Count == 5 && preview.Issues.Count == 1 && preview.Skipped == 2, "扫描须跳过联接和故障分支");
        Check(preview.Games.All(game => game.Existing is null), "初次扫描不得出现已有记录");
        var nightGame = preview.Games.Single(game => game.InstallDirectory == night);
        Check(nightGame.Suggested is not null, "匹配文件名的入口应预选");
        Check(nightGame.Metadata.Summary == localSummary && nightGame.Metadata.CoverPath == localCover,
            "预览应读到本地封面和多段简介");
        var nestedGame = preview.Games.Single(game => game.InstallDirectory == moon);
        Check(nestedGame.Suggested?.RelativePath.StartsWith("bin\\") == true, "归组程序子目录");
        Check(nestedGame.Metadata.Summary == "本地文本简介。" && !nestedGame.Metadata.HasCover, "读取文本简介");
        var ambiguous = preview.Games.Single(game => game.DisplayName.Contains("花"));
        Check(ambiguous.Suggested is null && ambiguous.Entries.Count(entry => entry.Auxiliary) == 2, "多入口与辅助程序分类");
        Check(await service.ListGamesAsync() is { Count: 0 }, "扫描预览不应写入数据库");

        using var cancellation = new CancellationTokenSource();
        await ExpectAsync<OperationCanceledException>(() =>
            service.ScanRootsAsync(roots, new SynchronousProgress<ScanProgress>(_ => cancellation.Cancel()), cancellation.Token));

        var missing = preview.Games.Single(game => game.DisplayName.Contains("Dream"));
        var missingEntry = missing.Suggested!;
        File.Move(missingEntry.Path, missingEntry.Path + ".saved");
        var remote = await TestCatalogAsync(files, imageBytes);
        var selected = preview.Games.Select(game => new ImportChoice(game, game.Suggested,
            game == nestedGame ? game.Metadata.FillMissing(remote) : null)).ToArray();
        await ExpectAsync<LibraryException>(() => service.ImportAsync(selected));
        Check((await service.ListGamesAsync()).Count == 0, "入口消失时整批保持未写入");
        File.Move(missingEntry.Path + ".saved", missingEntry.Path);

        Execute(db, """
            CREATE TRIGGER fail_import BEFORE INSERT ON games
            WHEN NEW.display_name LIKE '%Dream%' BEGIN SELECT RAISE(ABORT, 'test failure'); END;
            """);
        await ExpectAsync<LibraryException>(() => service.ImportAsync(selected));
        Check((await service.ListGamesAsync()).Count == 0, "中途写入失败应回滚此前插入");
        Execute(db, "DROP TRIGGER fail_import;");
        Check(await service.ImportAsync(selected) == new ImportResult(5, 0), "重试应提交全部五款游戏");
        Check(await service.ImportAsync(selected) == new ImportResult(0, 5), "再次提交须跳过已有数据");

        var gameToEdit = (await service.ListGamesAsync()).Single(game => game.InstallDirectory == night);
        var cached = gameToEdit.Metadata?.CoverPath;
        Check(cached is not null && cached != localCover && File.ReadAllBytes(cached).SequenceEqual(imageBytes)
            && gameToEdit.Metadata!.Summary == localSummary, "入库后封面与简介可离线读取");
        var enriched = (await service.ListGamesAsync()).Single(game => game.InstallDirectory == moon).Metadata!;
        Check(enriched.Summary == "本地文本简介。" && enriched.CoverPath == cached &&
            enriched.Provider == "VNDB" && enriched.ProviderId == "v4", "选定联网结果只补齐缺项并保存条目 ID");
        var resultFile = Path.Combine(home, "实际启动结果.json");
        string[] expectedArguments = ["", "含 空格", "line \"quote\"", "末尾\\", "  "];
        string[] probeArguments = ["--probe", resultFile, .. expectedArguments];
        var renamed = new GameDraft(gameToEdit.Id, "星の手紙 Letters", gameToEdit.InstallDirectory, gameToEdit.Executable,
            null, probeArguments);
        await service.SaveGameAsync(renamed);
        await AssertLaunchAsync(service, gameToEdit.Id, resultFile, gameToEdit.InstallDirectory, expectedArguments);
        var custom = Path.Combine(home, "自定义 Working Dir");
        Directory.CreateDirectory(custom);
        await service.SaveGameAsync(renamed with { WorkDirectory = custom });
        await AssertLaunchAsync(service, gameToEdit.Id, resultFile, custom, expectedArguments);

        var reopened = new SqliteLibrary(db, log);
        reopened.Initialize();
        var saved = reopened.FindGame(gameToEdit.Id)!;
        Check(saved.DisplayName == renamed.DisplayName && saved.WorkDirectory == custom &&
            saved.Arguments.SequenceEqual(probeArguments), "重启后恢复名称、工作目录和参数");
        Check(saved.Metadata == gameToEdit.Metadata, "旧调用编辑启动配置时应保留封面和简介");
        var duplicate = await service.ScanRootsAsync(roots, null, CancellationToken.None);
        Check(duplicate.Games.All(game => game.Existing is not null), "重叠根重扫须识别全部已有记录");
        Check((await service.ScanRootsAsync([roots.Single(item => item.Path == nested)], null, CancellationToken.None))
            .Games.Single().Existing?.InstallDirectory == moon, "从 bin 重扫应按相同执行文件去重");
        var outsideEntry = Path.Combine(outside, "Outside.exe");
        await ExpectAsync<LibraryException>(() => service.SaveGameAsync(renamed with { Executable = outsideEntry }));
        await ExpectAsync<LibraryException>(() => service.SaveGameAsync(renamed with
        {
            InstallDirectory = root, Executable = Path.Combine(junction, "Outside.exe")
        }));

        var moved = Path.Combine(home, "移动后 星の手紙");
        Directory.Move(night, moved);
        var checkedStates = new Dictionary<long, Availability>();
        await service.CheckGamesAsync(await service.ListGamesAsync(),
            new SynchronousProgress<(long Id, Availability State)[]>(batch =>
            {
                foreach (var (id, state) in batch)
                    checkedStates[id] = state;
            }), CancellationToken.None);
        Check(checkedStates.Values.Count(state => state.State == GameState.Unavailable) == 1 &&
            checkedStates.Values.Any(state => state.State == GameState.NeedsSetup), "状态区分不可用与待配置");
        await ExpectAsync<LibraryException>(() => service.LaunchAsync(gameToEdit.Id));
        var matched = await service.MatchRelocatedEntryAsync(saved, moved);
        Check(matched == Path.Combine(moved, saved.RelativeExecutable!), "迁移时匹配旧相对入口");
        await service.SaveGameAsync(renamed with { InstallDirectory = moved, Executable = matched });
        await AssertLaunchAsync(service, gameToEdit.Id, resultFile, moved, expectedArguments);
        await ExpectAsync<LibraryException>(() => service.SaveGameAsync(renamed with
        {
            InstallDirectory = moon, Executable = nestedGame.Suggested!.Path
        }));

        var manual = Path.Combine(home, "另一个同名游戏");
        Directory.CreateDirectory(manual);
        var badExe = Path.Combine(manual, "不可执行.exe");
        File.WriteAllText(badExe, "invalid executable");
        await service.SaveGameAsync(new GameDraft(null, saved.DisplayName, manual, badExe, null, []));
        var afterManual = await service.ListGamesAsync();
        Check(afterManual.Count(game => game.DisplayName == saved.DisplayName) == 2, "同名不同安装目录可共存");
        await ExpectAsync<LibraryException>(() => service.LaunchAsync(afterManual.Single(game => game.InstallDirectory == manual).Id));

        Execute(db, """
            CREATE TRIGGER fail_remove BEFORE DELETE ON games
            BEGIN SELECT RAISE(ABORT, 'test failure'); END;
            """);
        await ExpectAsync<LibraryException>(() => service.RemoveGameAsync(saved.Id));
        Check(reopened.FindGame(saved.Id) is not null, "删除失败时保留游戏记录");
        Execute(db, "DROP TRIGGER fail_remove;");
        await service.RemoveGameAsync(saved.Id);
        await service.RemoveGameAsync(saved.Id);
        Check(reopened.FindGame(saved.Id) is null && reopened.ListGames().Select(game => game.Id)
            .SequenceEqual(afterManual.Where(game => game.Id != saved.Id).Select(game => game.Id)),
            "移除仅删除指定记录，重开库后仍生效");
        Check(Directory.Exists(moved) && File.Exists(matched) && (await service.ListRootsAsync()).SequenceEqual(roots),
            "从库中移除应保留游戏文件和扫描目录");
        var rediscovered = (await service.ScanRootsAsync([new ScanRoot(0, moved)], null, CancellationToken.None)).Games.Single();
        Check(rediscovered.Existing is null && await service.ImportAsync([new ImportChoice(rediscovered,
            rediscovered.Entries.Single())]) == new ImportResult(1, 0), "移除后的游戏可以重新扫描并导入");

        await service.RemoveRootAsync(roots.Single(item => item.Path == root).Id);
        Check((await service.ListGamesAsync()).Count == 6, "移除扫描根应保留已入库记录");
        var diagnostics = File.ReadAllText(Path.Combine(data, "diagnostic.log"));
        Check(diagnostics.Contains("create-process") && !diagnostics.Contains("含 空格") && !diagnostics.Contains(root),
            "诊断允许记录系统错误码并保护路径和参数");
    }
    finally
    {
        Directory.Delete(junction);
    }

    var failedDb = Path.Combine(data, "failed-migration.db");
    Execute(failedDb, "CREATE TABLE games(value TEXT); INSERT INTO games VALUES ('prior data');");
    await ExpectAsync<LibraryException>(() => Task.Run(new SqliteLibrary(failedDb, log).Initialize));
    Check((string)Scalar(failedDb, "SELECT value FROM games")! == "prior data" &&
        (long)Scalar(failedDb, "SELECT count(*) FROM sqlite_master WHERE name = 'scan_roots'")! == 0,
        "迁移失败应保留旧数据并回滚新表");
    Execute(failedDb, "PRAGMA user_version = 99;");
    await ExpectAsync<LibraryException>(() => Task.Run(new SqliteLibrary(failedDb, log).Initialize));
    Check((long)Scalar(failedDb, "PRAGMA user_version")! == 99, "较新数据库保持原版本");
    Console.WriteLine("通过：扫描导入、封面与简介、资料查找、去重、持久化、传参、目录修复及游戏移除。");

    if (runBenchmark)
        await BenchmarkAsync(home, service);
}

static async Task<GameMetadata> TestCatalogAsync(MetadataFiles files, byte[] image)
{
    using var http = new HttpClient(new CatalogFixture(image));
    var catalog = new MetadataCatalog(http, files);
    var bangumi = (await catalog.SearchAsync(MetadataProvider.Bangumi, "CLANNAD", CancellationToken.None)).Single();
    Check(bangumi.Id == "13" && bangumi.Title == "测试中文名" && bangumi.Summary == "条目简介。",
        "Bangumi 读取封面与简介字段");
    var vndb = (await catalog.SearchAsync(MetadataProvider.Vndb, "CLANNAD", CancellationToken.None)).Single();
    var cover = await catalog.DownloadCoverAsync(vndb, CancellationToken.None);
    Check(vndb.Id == "v4" && vndb.Summary == "Story" && cover is not null, "VNDB 读取条目并缓存封面");
    Check(await catalog.DownloadCoverAsync(vndb with { CoverUrl = "https://example.test/cover.png" },
        CancellationToken.None) == cover, "跳过资料源以外的 URL，使用该来源提供的备用封面");
    return new GameMetadata(vndb.Summary, cover, "VNDB", vndb.Id);
}

static void CreateFixture(string root)
{
    var night = Path.Combine(root, "01 星降る夜");
    var nested = Path.Combine(root, "02 月影 Lantern", "bin");
    var ambiguous = Path.Combine(root, "03 花の手紙");
    var dream = Path.Combine(root, "04 Lucid Dream");
    var aurora = Path.Combine(root, "05 极光之庭");
    foreach (var path in new[] { night, nested, ambiguous, dream, aurora })
        Directory.CreateDirectory(path);

    foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(file => !file.EndsWith(".exe")))
        File.Copy(file, Path.Combine(night, Path.GetFileName(file)), overwrite: true);
    var host = Path.Combine(AppContext.BaseDirectory, "GalgameManager.Smoke.exe");
    foreach (var path in new[]
    {
        Path.Combine(night, "01 星降る夜.exe"), Path.Combine(nested, "02 月影 Lantern.exe"),
        Path.Combine(ambiguous, "hanako.exe"), Path.Combine(ambiguous, "hanako_CN.exe"),
        Path.Combine(dream, "04 Lucid Dream.exe"), Path.Combine(aurora, "05 极光之庭.exe")
    })
    {
        File.Copy(host, path, overwrite: true);
    }
    File.WriteAllText(Path.Combine(ambiguous, "config.exe"), "");
    File.WriteAllText(Path.Combine(ambiguous, "unins000.exe"), "");
    File.WriteAllText(Path.Combine(ambiguous, "resources.xp3"), "");
}

static async Task BenchmarkAsync(string home, LibraryService service)
{
    var synthetic = Path.Combine(home, "benchmark");
    for (var i = 0; i < 5000; i++)
    {
        var directory = Path.Combine(synthetic, $"Folder-{i:D4}");
        Directory.CreateDirectory(directory);
        if (i % 5 == 0)
            File.WriteAllText(Path.Combine(directory, $"Folder-{i:D4}.exe"), "");
    }

    var roots = new[] { new ScanRoot(0, synthetic) };
    var elapsed = new List<double>();
    for (var i = 0; i < 3; i++)
    {
        var timer = Stopwatch.StartNew();
        var result = await service.ScanRootsAsync(roots, null, CancellationToken.None);
        Check(result.Directories == 5001 && result.Games.Count == 1000, "合成样本完整性");
        elapsed.Add(timer.Elapsed.TotalMilliseconds);
    }
    Console.WriteLine($"基线：5,001 个目录、1,000 个入口，{string.Join(" / ", elapsed.Select(ms => $"{ms:F0} ms"))}，中位数 {elapsed.Order().ElementAt(1):F0} ms。");
}

static async Task AssertLaunchAsync(LibraryService service, long id, string report, string directory, string[] arguments)
{
    File.Delete(report);
    await service.LaunchAsync(id);
    for (var attempt = 0; attempt < 160; attempt++)
    {
        if (File.Exists(report))
        {
            try
            {
                var actual = JsonSerializer.Deserialize<Probe>(await File.ReadAllTextAsync(report));
                Check(actual?.Directory == directory && actual.Arguments.SequenceEqual(arguments), "游戏收到的参数及工作目录");
                // The probe writes before it exits. Wait for Windows to release its installation directory.
                try
                {
                    using var process = Process.GetProcessById(actual!.ProcessId);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
                }
                catch (ArgumentException) { } // It has already exited.
                return;
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        await Task.Delay(25);
    }
    throw new InvalidOperationException("测试进程未在期限内写出结果");
}

static async Task ExpectAsync<TError>(Func<Task> action) where TError : Exception
{
    try { await action(); }
    catch (TError) { return; }
    throw new InvalidOperationException($"应抛出 {typeof(TError).Name}");
}

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}

static void Execute(string database, string sql) => Scalar(database, sql);

static object? Scalar(string database, string sql)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar();
}

sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

sealed record Probe(string Directory, string[] Arguments, int ProcessId);

sealed class CatalogFixture(byte[] cover) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpContent content = request.RequestUri!.Host switch
        {
            "api.bgm.tv" => new StringContent("""
                {"data":[{"id":13,"name":"CLANNAD","name_cn":"测试中文名","date":"2004-04-28",
                "summary":"条目简介。","images":{"large":"https://lain.bgm.tv/example.jpg"}}]}
                """),
            "api.vndb.org" => new StringContent("""
                {"results":[{"id":"v4","title":"CLANNAD","released":"2004-04-28",
                "description":"Story","image":{"url":"https://t.vndb.org/missing.png","thumbnail":"https://t.vndb.org/example.png"}}]}
                """),
            "t.vndb.org" when request.RequestUri.AbsolutePath == "/missing.png" => throw new HttpRequestException("Simulated missing image."),
            "t.vndb.org" => new ByteArrayContent(cover),
            _ => throw new InvalidOperationException("Unexpected metadata request.")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
