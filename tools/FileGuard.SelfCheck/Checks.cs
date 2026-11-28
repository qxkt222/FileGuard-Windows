using System.Diagnostics;
using System.Runtime.CompilerServices;
using FileGuard.Core.Control;
using FileGuard.Core.Logging;
using FileGuard.Core.Models;
using FileGuard.Core.Rules;
using FileGuard.Core.Storage;
using FileGuard.Core.Watching;
using FileGuard.Service;
using FileGuard.WinFsp;

namespace FileGuard.SelfCheck;

/// <summary>
/// 功能自检清单。每条断言对应 README 里声称的一项能力，
/// 通过公开 API 真调，不依赖测试项目里的私有夹具。
/// 依赖管理员 / 服务 / 真实用户进程的项标 SKIP，并写明前置条件。
/// </summary>
internal static class Checks
{
    private static bool WinFspInstalled =>
        File.Exists(@"C:\Program Files (x86)\WinFsp\bin\winfsp-x64.dll");

    public static void Register(CheckRegistry registry)
    {
        Build(registry);
        Defaults(registry);
        PathRuleChecks(registry);
        ConfigChecks(registry);
        LogChecks(registry);
        QuarantineChecks(registry);
        RuleFileChecks(registry);
        ProtocolChecks(registry);
        ThrottleChecks(registry);
        RuntimeChecks(registry);
        GuardPolicyChecks(registry);
        VirtualDiskChecks(registry);
        PublishChecks(registry);
    }

    // ---------------------------------------------------------------- 构建

    private static void Build(CheckRegistry registry)
    {
        var core = typeof(GuardEntry).Assembly;
        var service = typeof(GuardPolicy).Assembly;

        registry.That("构建", "Core 程序集可加载",
            () => core.GetName().Name == "FileGuard.Core",
            $"程序集名是 {core.GetName().Name}");

        registry.That("构建", "Service 程序集可加载",
            () => service.GetName().Name == "FileGuard.Service",
            $"程序集名是 {service.GetName().Name}");
    }

    // ------------------------------------------------------------ 默认值

    private static void Defaults(CheckRegistry registry)
    {
        var entry = new GuardEntry();

        registry.Equal("默认值", "单文件上限默认 20 KB", entry.MaxBytes, 20L * 1024);
        registry.Equal("默认值", "条目默认监视模式", entry.Mode, GuardMode.Watch);
        registry.That("默认值", "条目默认启用且限制生效",
            () => entry.Enabled && entry.LimitEnabled);
        registry.That("默认值", "条目默认只拦新建文件",
            () => entry.WatchWrite && !entry.WatchCreate && !entry.WatchRename
                  && !entry.CheckExistingFiles,
            "默认事件应为「写入」开、其余关，且不检查存量");

        var options = new GuardOptions();
        registry.Equal("默认值", "隔离区默认保留 50 次", options.QuarantineKeep, 50);
        registry.Equal("默认值", "规则文件名默认 .guard-rules.md", options.RuleFileName, ".guard-rules.md");
        registry.Equal("默认值", "面板默认显示 5 条", options.RecentRecordCount, 5);
        registry.That("默认值", "默认开启隔离区与非管理员限制",
            () => options.EnableQuarantine && options.NonAdminLock);

        var clone = entry.Clone();
        clone.ExemptPaths.Add(@"D:\x");
        registry.That("默认值", "Clone 深拷贝豁免目录",
            () => entry.ExemptPaths.Count == 0,
            "改克隆体的豁免列表不应影响原条目");

        // 「删除事件」开关从未产生过任何行为，已从模型和界面移除。
        registry.That("默认值", "已移除无效的删除事件开关",
            () => typeof(GuardEntry).GetProperty("WatchDelete") is null);
    }

    // ------------------------------------------------------------ 路径规则

    private static void PathRuleChecks(CheckRegistry registry)
    {
        var rules = PathRules.Default;
        const string root = @"D:\proj";

        registry.That("路径规则", "忽略 .git 目录",
            () => rules.IsIgnoredPath(@"D:\proj\.git\config", root));

        registry.That("路径规则", "忽略 node_modules",
            () => rules.IsIgnoredPath(@"D:\proj\node_modules\a\index.js", root));

        registry.That("路径规则", "忽略锁文件 package-lock.json",
            () => rules.IsIgnoredPath(@"D:\proj\package-lock.json", root));

        registry.That("路径规则", "忽略 .tmp / .lock 后缀",
            () => rules.IsIgnoredPath(@"D:\proj\a.tmp", root)
                  && rules.IsIgnoredPath(@"D:\proj\b.lock", root));

        registry.That("路径规则", "普通源码不忽略",
            () => !rules.IsIgnoredPath(@"D:\proj\src\main.cs", root),
            "src/main.cs 应被监视");

        registry.That("路径规则", "目录名大小写不敏感",
            () => rules.IsIgnoredPath(@"D:\proj\NODE_MODULES\a.js", root));

        registry.That("路径规则", "根目录外的相对路径视为忽略",
            () => rules.IsIgnoredPath(@"D:\other\a.cs", root),
            @"相对路径以 .. 开头时应忽略");

        registry.That("路径规则", "文本扩展名白名单",
            () => rules.IsTextFile("a.cs") && rules.IsTextFile("b.ps1")
                  && rules.IsTextFile("c.md") && rules.IsTextFile("d.json"),
            ".cs/.ps1/.md/.json 应算文本");

        registry.That("路径规则", "二进制扩展名不监视",
            () => !rules.IsTextFile("a.png") && !rules.IsTextFile("b.exe")
                  && !rules.IsTextFile("c.zip"),
            "图片/可执行/压缩包不应算文本");

        // README 的「二进制文件不管」是有意设计；无扩展名文件同样不管。
        registry.That("路径规则", "无扩展名文件不监视（已知限制）",
            () => !rules.IsTextFile("Dockerfile") && !rules.IsTextFile("Makefile"),
            "Dockerfile/Makefile 这类无扩展名文本不会进白名单，属 README 已知边界");

        registry.That("路径规则", "豁免目录含自身且不区分大小写",
            () => PathRules.IsUnder(@"D:\proj\KEEP\", @"d:\proj\keep")
                  && PathRules.IsUnder(@"D:\proj\keep\sub\a.cs", @"D:\proj\keep"));

        registry.That("路径规则", "豁免目录不误伤前缀相同的兄弟目录",
            () => !PathRules.IsUnder(@"D:\proj\keepx\a.cs", @"D:\proj\keep"),
            @"keepx 不应被当成 keep 的子目录");
    }

    // -------------------------------------------------------------- 配置

    private static void ConfigChecks(CheckRegistry registry)
    {
        registry.That("配置", "配置文件不存在时返回默认配置", () =>
        {
            var dir = NewTempDir();
            try
            {
                var config = new ConfigStore(Path.Combine(dir, "config.json")).Load();
                return config.Entries.Count == 0
                       && config.Options.QuarantineKeep == 50
                       && config.Options.DefaultMaxBytes == 20 * 1024;
            }
            finally { Cleanup(dir); }
        });

        registry.That("配置", "保存后能原样读回", () =>
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                var store = new ConfigStore(path);
                var original = new GuardConfig();
                original.Entries.Add(new GuardEntry
                {
                    RootPath = @"D:\proj",
                    Mode = GuardMode.VirtualDisk,
                    MaxBytes = 4096,
                    ExemptPaths = { @"D:\proj\keep" },
                });
                store.Save(original);

                var loaded = store.Load();
                return loaded.Entries.Count == 1
                       && loaded.Entries[0].RootPath == @"D:\proj"
                       && loaded.Entries[0].Mode == GuardMode.VirtualDisk
                       && loaded.Entries[0].MaxBytes == 4096
                       && loaded.Entries[0].ExemptPaths.Count == 1;
            }
            finally { Cleanup(dir); }
        });

        registry.That("配置", "缺少 mode 字段按监视模式读", () =>
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path,
                    """{"entries":[{"rootPath":"D:\\proj","maxBytes":1024}]}""");
                var config = new ConfigStore(path).Load();
                return config.Entries.Count == 1 && config.Entries[0].Mode == GuardMode.Watch;
            }
            finally { Cleanup(dir); }
        });

        registry.That("配置", "mode 写成字符串 virtualDisk 能读", () =>
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path,
                    """{"entries":[{"rootPath":"D:\\proj","mode":"virtualDisk"}]}""");
                var config = new ConfigStore(path).Load();
                return config.Entries.Count == 1
                       && config.Entries[0].Mode == GuardMode.VirtualDisk;
            }
            finally { Cleanup(dir); }
        });

        registry.That("配置", "枚举写成人可读字符串", () =>
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                var store = new ConfigStore(path);
                var config = new GuardConfig();
                config.Entries.Add(new GuardEntry { RootPath = @"D:\proj", Mode = GuardMode.VirtualDisk });
                store.Save(config);
                var json = File.ReadAllText(path);
                return json.Contains("\"virtualDisk\"", StringComparison.Ordinal)
                       && !json.Contains("\"mode\": 1", StringComparison.Ordinal);
            }
            finally { Cleanup(dir); }
        });

        registry.That("配置", "损坏的 JSON 不抛异常，回退默认", () =>
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path, "{ this is not json");
                var config = new ConfigStore(path).Load();
                return config.Entries.Count == 0;
            }
            finally { Cleanup(dir); }
        });

        registry.That("配置", "保存后不残留 .tmp 文件", () =>
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                new ConfigStore(path).Save(new GuardConfig());
                return File.Exists(path) && !File.Exists(path + ".tmp");
            }
            finally { Cleanup(dir); }
        });
    }

    // ------------------------------------------------------------ 拦截日志

    private static void LogChecks(CheckRegistry registry)
    {
        registry.That("拦截日志", "内存只保留最近 5 条", () =>
        {
            var dir = NewTempDir();
            try
            {
                var log = new InterceptLog(Path.Combine(dir, "intercept.log"), 5);
                for (var i = 0; i < 8; i++) log.Append(Record($"file{i}.cs"));
                var recent = log.Recent;
                return recent.Count == 5
                       && recent[0].Path.EndsWith("file3.cs", StringComparison.Ordinal)
                       && recent[^1].Path.EndsWith("file7.cs", StringComparison.Ordinal);
            }
            finally { Cleanup(dir); }
        });

        registry.That("拦截日志", "文件里保留全部 8 条", () =>
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "intercept.log");
                var log = new InterceptLog(path, 5);
                for (var i = 0; i < 8; i++) log.Append(Record($"file{i}.cs"));
                return File.ReadAllLines(path).Length == 8;
            }
            finally { Cleanup(dir); }
        });

        registry.That("拦截日志", "追加时触发 Changed 事件", () =>
        {
            var dir = NewTempDir();
            try
            {
                var log = new InterceptLog(Path.Combine(dir, "intercept.log"), 5);
                var fired = 0;
                log.Changed += (_, _) => fired++;
                log.Append(Record("a.cs"));
                return fired == 1;
            }
            finally { Cleanup(dir); }
        });

        registry.That("拦截日志", "超过 1 MB 滚动到 .1", () =>
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "intercept.log");
                File.WriteAllBytes(path, new byte[1024 * 1024 + 64]);
                var log = new InterceptLog(path, 5);
                log.Append(Record("a.cs"));
                return File.Exists(path + ".1")
                       && new FileInfo(path).Length < 1024 * 1024;
            }
            finally { Cleanup(dir); }
        });

        registry.That("拦截日志", "记录行包含动作、大小与路径", () =>
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "intercept.log");
                var log = new InterceptLog(path, 5);
                log.Append(Record(@"D:\proj\big.cs"));
                var line = File.ReadAllText(path);
                return line.Contains("[Deleted]", StringComparison.Ordinal)
                       && line.Contains("big.cs", StringComparison.Ordinal)
                       && line.Contains("5000/1024", StringComparison.Ordinal);
            }
            finally { Cleanup(dir); }
        });
    }

    // -------------------------------------------------------------- 隔离区

    private static void QuarantineChecks(CheckRegistry registry)
    {
        registry.That("隔离区", "备份内容与源文件一致", () =>
        {
            var dir = NewTempDir();
            try
            {
                var watch = Path.Combine(dir, "watch");
                Directory.CreateDirectory(watch);
                var source = Path.Combine(watch, "big.cs");
                File.WriteAllText(source, "payload");

                var store = new QuarantineStore(Path.Combine(dir, "q"));
                var dest = store.Backup(source, Guid.NewGuid(), watch);
                return dest is not null && File.Exists(dest) && File.ReadAllText(dest) == "payload";
            }
            finally { Cleanup(dir); }
        });

        registry.That("隔离区", "备份路径始终落在隔离区根目录内", () =>
        {
            var dir = NewTempDir();
            try
            {
                var watch = Path.Combine(dir, "watch");
                var outside = Path.Combine(dir, "outside");
                Directory.CreateDirectory(watch);
                Directory.CreateDirectory(outside);
                var source = Path.Combine(outside, "evil.cs");
                File.WriteAllText(source, "x");

                var root = Path.Combine(dir, "q");
                var dest = new QuarantineStore(root).Backup(source, Guid.NewGuid(), watch);
                return dest is not null
                       && dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                       && !dest.Contains("..", StringComparison.Ordinal);
            }
            finally { Cleanup(dir); }
        });

        registry.That("隔离区", "Prune 只保留最近 5 个批次", () =>
        {
            var dir = NewTempDir();
            try
            {
                var watch = Path.Combine(dir, "watch");
                Directory.CreateDirectory(watch);
                var source = Path.Combine(watch, "a.cs");
                File.WriteAllText(source, "x");

                var store = new QuarantineStore(Path.Combine(dir, "q"));
                for (var i = 0; i < 6; i++)
                {
                    store.Backup(source, Guid.NewGuid(), watch);
                    Thread.Sleep(5);
                }

                var before = store.ListBatches().Count;
                store.Prune(5);
                return before == 6 && store.ListBatches().Count == 5;
            }
            finally { Cleanup(dir); }
        });
    }

    // ------------------------------------------------------------ 规则文件

    private static void RuleFileChecks(CheckRegistry registry)
    {
        registry.That("规则文件", "按上限生成 .guard-rules.md", () =>
        {
            var dir = NewTempDir();
            try
            {
                var entry = new GuardEntry { RootPath = dir, MaxBytes = 2048 };
                var path = RuleFileWriter.Write(dir, entry, 50);
                var text = File.ReadAllText(path);
                return Path.GetFileName(path) == ".guard-rules.md"
                       && text.Contains("2 KB", StringComparison.Ordinal)
                       && text.Contains("2048 字节", StringComparison.Ordinal);
            }
            finally { Cleanup(dir); }
        });

        // 观察模式不会删文件，规则文件却无条件写「会被立即删除」。
        registry.That("规则文件", "观察模式的文案不提删除", () =>
        {
            var dir = NewTempDir();
            try
            {
                var entry = new GuardEntry { RootPath = dir, MaxBytes = 2048, LimitEnabled = false };
                var text = File.ReadAllText(RuleFileWriter.Write(dir, entry, 50));
                return !text.Contains("立即删除", StringComparison.Ordinal);
            }
            finally { Cleanup(dir); }
        }, "LimitEnabled=false 时文件不会被删，文案仍写「立即删除」（RuleFileWriter.cs:23）");
    }

    // ------------------------------------------------------------ 控制协议

    private static void ProtocolChecks(CheckRegistry registry)
    {
        registry.That("控制协议", "hello 往返保留 pid",
            () =>
            {
                var back = GuardProtocol.Deserialize(GuardProtocol.Serialize(GuardMessage.Hello(1234)));
                return back is not null && back.Type == GuardMessageTypes.Hello && back.Pid == 1234;
            });

        registry.That("控制协议", "非法 JSON 返回 null 不抛异常",
            () => GuardProtocol.Deserialize("oops") is null
                  && GuardProtocol.Deserialize("") is null
                  && GuardProtocol.Deserialize("{\"type\":123}") is null);

        registry.That("控制协议", "status-ack 的 appPid 字段即使为 null 也写出",
            () =>
            {
                var json = GuardProtocol.Serialize(GuardMessage.StatusAck(true, null));
                return json.Contains("\"appPid\":null", StringComparison.Ordinal);
            });

        registry.That("控制协议", "空字段序列化时省略",
            () =>
            {
                var json = GuardProtocol.Serialize(GuardMessage.Hello(1));
                return !json.Contains("\"reason\"", StringComparison.Ordinal)
                       && !json.Contains("\"guarding\"", StringComparison.Ordinal);
            });

        registry.That("控制协议", "类型写成小写字符串",
            () => GuardProtocol.Serialize(GuardMessage.Hello(1))
                .Contains("\"type\":\"hello\"", StringComparison.Ordinal));

        registry.That("控制协议", "八种消息类型常量齐全",
            () =>
            {
                var all = new[]
                {
                    GuardMessageTypes.Hello, GuardMessageTypes.HelloAck,
                    GuardMessageTypes.ExitRequest, GuardMessageTypes.ExitApproved,
                    GuardMessageTypes.ExitDenied, GuardMessageTypes.Status,
                    GuardMessageTypes.StatusAck, GuardMessageTypes.Error,
                };
                return all.Distinct().Count() == 8;
            });
    }

    // ------------------------------------------------------------ 通知节流

    private static void ThrottleChecks(CheckRegistry registry)
    {
        registry.That("通知节流", "同一路径 3 秒内只通知一次", () =>
        {
            var throttle = new PathThrottle();
            return throttle.ShouldNotify(@"D:\proj\big.cs")
                   && !throttle.ShouldNotify(@"D:\proj\big.cs");
        });

        registry.That("通知节流", "不同路径互不影响", () =>
        {
            var throttle = new PathThrottle();
            return throttle.ShouldNotify(@"D:\proj\a.cs")
                   && throttle.ShouldNotify(@"D:\proj\b.cs");
        });

        registry.That("通知节流", "路径大小写不敏感", () =>
        {
            var throttle = new PathThrottle();
            return throttle.ShouldNotify(@"D:\Proj\A.cs")
                   && !throttle.ShouldNotify(@"d:\proj\a.cs");
        });
    }

    // ------------------------------------------------------------ 监视引擎

    private static void RuntimeChecks(CheckRegistry registry)
    {
        registry.That("监视引擎", "超限新文件被删除并记录 Deleted", () =>
        {
            using var ctx = new RuntimeContext();
            var entry = ctx.WatchEntry(maxBytes: 1024);
            var path = Path.Combine(ctx.Watch, "big.cs");
            ctx.Start(entry);
            File.WriteAllBytes(path, new byte[4096]);
            return WaitUntil(() => !File.Exists(path), 5000)
                   && ctx.Log.Recent.Any(r => r.Path == path && r.Action == InterceptAction.Deleted);
        });

        registry.That("监视引擎", "未超限文件保留且不记录", () =>
        {
            using var ctx = new RuntimeContext();
            var entry = ctx.WatchEntry(maxBytes: 1024);
            var path = Path.Combine(ctx.Watch, "small.cs");
            ctx.Start(entry);
            File.WriteAllBytes(path, new byte[512]);
            Thread.Sleep(700);
            return File.Exists(path) && ctx.Log.Recent.Count == 0;
        });

        registry.That("监视引擎", "观察模式保留文件并记录 Observed", () =>
        {
            using var ctx = new RuntimeContext();
            var entry = ctx.WatchEntry(maxBytes: 1024, limit: false);
            var path = Path.Combine(ctx.Watch, "big.cs");
            ctx.Start(entry);
            File.WriteAllBytes(path, new byte[4096]);
            var recorded = WaitUntil(
                () => ctx.Log.Recent.Any(r => r.Path == path), 3000);
            Thread.Sleep(300);
            return recorded && File.Exists(path)
                   && ctx.Log.Recent.Any(r => r.Path == path && r.Action == InterceptAction.Observed);
        });

        registry.That("监视引擎", "豁免目录里的超限文件不删", () =>
        {
            using var ctx = new RuntimeContext();
            var exempt = Path.Combine(ctx.Watch, "keep");
            Directory.CreateDirectory(exempt);
            var entry = ctx.WatchEntry(maxBytes: 1024);
            entry.ExemptPaths.Add(exempt);
            var path = Path.Combine(exempt, "big.cs");
            ctx.Start(entry);
            File.WriteAllBytes(path, new byte[4096]);
            Thread.Sleep(1000);
            return File.Exists(path);
        });

        registry.That("监视引擎", "虚拟盘条目不起监视 watcher", () =>
        {
            using var ctx = new RuntimeContext();
            var entry = ctx.WatchEntry(maxBytes: 1024);
            entry.Mode = GuardMode.VirtualDisk;
            ctx.Runtime.Apply(new[] { entry });
            ctx.Runtime.Start();
            return !ctx.Runtime.IsEntryActive(entry.Id);
        });

        registry.That("监视引擎", "删除前写入隔离区", () =>
        {
            using var ctx = new RuntimeContext();
            var entry = ctx.WatchEntry(maxBytes: 1024);
            var path = Path.Combine(ctx.Watch, "big.cs");
            ctx.Start(entry);
            File.WriteAllBytes(path, new byte[4096]);
            var deleted = WaitUntil(() => !File.Exists(path), 5000);
            return deleted && ctx.Quarantine.ListBatches().Count > 0;
        });

        registry.That("监视引擎", "多条目可同时运行，各按自己的模式", () =>
        {
            using var ctx = new RuntimeContext();
            var dir2 = Path.Combine(ctx.Root, "watch2");
            var dir3 = Path.Combine(ctx.Root, "watch3");
            Directory.CreateDirectory(dir2);
            Directory.CreateDirectory(dir3);

            var watchEntry = ctx.WatchEntry(maxBytes: 1024);
            var diskEntry = new GuardEntry { RootPath = dir2, Mode = GuardMode.VirtualDisk };
            var disabled = new GuardEntry { RootPath = dir3, Enabled = false };

            ctx.Runtime.Apply(new[] { watchEntry, diskEntry, disabled });
            ctx.Runtime.Start();

            return ctx.Runtime.IsEntryActive(watchEntry.Id)
                   && !ctx.Runtime.IsEntryActive(diskEntry.Id)
                   && !ctx.Runtime.IsEntryActive(disabled.Id);
        });

        // 「已存在的文件也检查」必须在启动时就扫描存量。
        registry.That("监视引擎", "开启检查存量后启动即处理超限旧文件", () =>
        {
            using var ctx = new RuntimeContext();
            var path = Path.Combine(ctx.Watch, "preexisting.cs");
            File.WriteAllBytes(path, new byte[4096]);

            var entry = ctx.WatchEntry(maxBytes: 1024);
            entry.CheckExistingFiles = true;
            ctx.Start(entry);

            return WaitUntil(() => !File.Exists(path), 2000);
        }, "启动时只把存量记为基线（FolderWatcher.cs:39），不扫描；旧文件要再次被写才会检查");
    }

    // ------------------------------------------------------------ 守护决策

    private static void GuardPolicyChecks(CheckRegistry registry)
    {
        registry.That("守护决策", "主程序在跑就不拉起", () =>
        {
            var d = GuardPolicy.Decide(appRunning: true, exitApproved: false, TimeSpan.Zero);
            return !d.ShouldLaunch;
        });

        registry.That("守护决策", "主程序消失且未批准退出就拉起", () =>
        {
            var d = GuardPolicy.Decide(appRunning: false, exitApproved: false, TimeSpan.Zero);
            return d.ShouldLaunch;
        });

        registry.That("守护决策", "已批准退出且主程序已消失：不拉起也不清批准", () =>
        {
            var d = GuardPolicy.Decide(appRunning: false, exitApproved: true, TimeSpan.FromMinutes(10));
            return !d.ShouldLaunch && !d.ResetApproval;
        });

        registry.That("守护决策", "批准后宽限期内不拉起不清批准", () =>
        {
            var d = GuardPolicy.Decide(appRunning: true, exitApproved: true, TimeSpan.FromSeconds(10));
            return !d.ShouldLaunch && !d.ResetApproval;
        });

        registry.That("守护决策", "批准后超过宽限期仍未 hello 则清批准", () =>
        {
            var d = GuardPolicy.Decide(
                appRunning: true, exitApproved: true, GuardPolicy.ApprovalGracePeriod + TimeSpan.FromSeconds(1));
            return !d.ShouldLaunch && d.ResetApproval;
        });

        registry.Skip("守护决策", "非管理员退出请求被拒",
            "需要真实管道调用方进程与 SCM；由 tests/FileGuard.Service.Tests 覆盖");
        registry.Skip("守护决策", "服务每 2 秒拉起主程序",
            "SKIP: 需安装并运行 FileGuardGuard 服务");
    }

    // -------------------------------------------------------------- 虚拟盘

    private static void VirtualDiskChecks(CheckRegistry registry)
    {
        if (!WinFspInstalled)
        {
            registry.Skip("虚拟盘", "WinFsp 驱动与原生库", "未检测到 C:\\Program Files (x86)\\WinFsp");
            registry.Skip("虚拟盘", "Fsp.Interop.Api 静态构造", "未安装 WinFsp");
            registry.Skip("虚拟盘", "挂载 / 卸载 / junction", "未安装 WinFsp");
            registry.Skip("虚拟盘", "超限写入返回磁盘已满", "未安装 WinFsp");
            registry.Skip("虚拟盘", "只读文件可以通过挂载点读取", "未安装 WinFsp");
            return;
        }

        registry.That("虚拟盘", "WinFsp 驱动与原生库存在",
            () => WinFspInstalled
                  && File.Exists(@"C:\Program Files (x86)\WinFsp\bin\winfsp-msil.dll"));

        // 0.1.0 的挂载失败就是这个静态构造抛 TypeInitializationException。
        registry.Runs("虚拟盘", "Fsp.Interop.Api 静态构造通过", () =>
        {
            // 自检项目不直接引用 winfsp-msil，通过 FileGuard.WinFsp 的引用加载。
            var name = typeof(VirtualDisk).Assembly.GetReferencedAssemblies()
                           .FirstOrDefault(n => n.Name == "winfsp-msil")
                       ?? throw new InvalidOperationException("FileGuard.WinFsp 没有引用 winfsp-msil");
            var api = System.Reflection.Assembly.Load(name).GetType("Fsp.Interop.Api")
                      ?? throw new InvalidOperationException("找不到 Fsp.Interop.Api");
            RuntimeHelpers.RunClassConstructor(api.TypeHandle);
        });

        registry.That("虚拟盘", "挂载后原路径成为 junction，卸载后恢复", () =>
        {
            var root = NewTempDir();
            var real = Path.Combine(root, "proj");
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(real, "seed.txt"), "seed");
            VirtualDisk? disk = null;
            try
            {
                disk = VirtualDisk.Mount(real, 4096);
                var mounted = disk.IsMounted
                              && (File.GetAttributes(real) & FileAttributes.ReparsePoint) != 0
                              && Directory.Exists(disk.BackingStore);

                disk.Unmount();
                disk = null;

                return mounted
                       && Directory.Exists(real)
                       && (File.GetAttributes(real) & FileAttributes.ReparsePoint) == 0
                       && !Directory.Exists(real + ".__guard_store")
                       && File.ReadAllText(Path.Combine(real, "seed.txt")) == "seed";
            }
            finally
            {
                try { disk?.Unmount(); } catch { }
                Cleanup(root);
            }
        });

        registry.That("虚拟盘", "超限写入返回磁盘已满且文件不落盘", () =>
        {
            var root = NewTempDir();
            var real = Path.Combine(root, "proj");
            Directory.CreateDirectory(real);
            VirtualDisk? disk = null;
            try
            {
                disk = VirtualDisk.Mount(real, 4096);
                var target = Path.Combine(real, "big.cs");
                IOException? error = null;
                try
                {
                    using var stream = new FileStream(target, FileMode.Create, FileAccess.Write,
                        FileShare.Read | FileShare.Write | FileShare.Delete, 1);
                    stream.Write(new byte[4096], 0, 4096);
                    stream.Write(new byte[4096], 0, 4096);
                }
                catch (IOException ex) { error = ex; }

                return error is not null
                       && (error.HResult & 0xFFFF) == 112
                       && WaitUntil(() => !File.Exists(target), 3000);
            }
            finally
            {
                try { disk?.Unmount(); } catch { }
                Cleanup(root);
            }
        });

        // LimitedPassthrough.Open 恒用 ReadWrite，只读文件连读都过不去。
        registry.That("虚拟盘", "只读文件可以通过挂载点读取", () =>
        {
            var root = NewTempDir();
            var real = Path.Combine(root, "proj");
            Directory.CreateDirectory(real);
            var readOnly = Path.Combine(real, "readonly.txt");
            File.WriteAllText(readOnly, "read-me");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            VirtualDisk? disk = null;
            try
            {
                disk = VirtualDisk.Mount(real, 4096);
                try
                {
                    return File.ReadAllText(Path.Combine(real, "readonly.txt")) == "read-me";
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
            finally
            {
                try { disk?.Unmount(); } catch { }
                try { File.SetAttributes(readOnly, FileAttributes.Normal); } catch { }
                Cleanup(root);
            }
        }, "LimitedPassthrough.cs:476 Open 恒用 FileAccess.ReadWrite，忽略 GrantedAccess");
    }

    // ------------------------------------------------------------ 发布产物

    private static void PublishChecks(CheckRegistry registry)
    {
        var repo = FindRepoRoot();
        if (repo is null)
        {
            registry.Skip("发布产物", "定位仓库根目录", "找不到 FileGuard.sln，跳过产物检查");
            return;
        }

        // 没构建过发布产物不是代码缺陷（CI 里也不会生成安装包）。
        if (!Directory.Exists(Path.Combine(repo, "publish", "app-full"))
            || !Directory.Exists(Path.Combine(repo, "dist")))
        {
            registry.Skip("发布产物", "检查发布产物", "本地未构建 publish/dist，跳过");
            return;
        }

        registry.That("发布产物", "publish/app-full/FileGuard.exe 存在",
            () => File.Exists(Path.Combine(repo, "publish", "app-full", "FileGuard.exe")));

        // 这条防的是 0.1.0 的回归：msil 必须在 exe 旁边，不能进 bundle。
        registry.That("发布产物", "winfsp-msil.dll 作为独立文件随包发布",
            () => File.Exists(Path.Combine(repo, "publish", "app-full", "winfsp-msil.dll")),
            "单文件 bundle 里 Fsp.Interop.Api 的静态构造会抛 TypeInitializationException（0.1.0 的故障）");

        registry.That("发布产物", "安装脚本携带 winfsp-msil.dll", () =>
        {
            var iss = Path.Combine(repo, "installer", "FileGuard.iss");
            return File.Exists(iss)
                   && File.ReadAllText(iss).Contains("winfsp-msil.dll", StringComparison.OrdinalIgnoreCase);
        });

        registry.That("发布产物", "版本号在 props 与安装脚本中一致", () =>
        {
            var props = Path.Combine(repo, "Directory.Build.props");
            var iss = Path.Combine(repo, "installer", "FileGuard.iss");
            if (!File.Exists(props) || !File.Exists(iss)) return false;

            var propsVersion = Extract(props, @"<Version>([^<]+)</Version>");
            var issVersion = Extract(iss, "#define MyAppVersion \"([^\"]+)\"");
            return propsVersion is not null && propsVersion == issVersion;
        }, "Directory.Build.props 的 <Version> 与 FileGuard.iss 的 MyAppVersion 不一致");

        registry.That("发布产物", "dist 目录有安装包", () =>
            Directory.Exists(Path.Combine(repo, "dist"))
            && Directory.EnumerateFiles(Path.Combine(repo, "dist"), "FileGuard-*-setup.exe").Any());
    }

    // ------------------------------------------------------------- helpers

    private static InterceptRecord Record(string fileName) => new(
        DateTimeOffset.Now,
        Guid.NewGuid(),
        @"D:\proj\" + fileName,
        5000,
        1024,
        InterceptAction.Deleted);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fg-selfcheck", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* 临时目录清不掉不影响结论 */ }
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "FileGuard.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static string? Extract(string path, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path), pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>每个监视引擎用例一套独立目录 + 运行时，结尾统一释放。</summary>
    private sealed class RuntimeContext : IDisposable
    {
        public RuntimeContext()
        {
            Root = NewTempDir();
            Watch = Path.Combine(Root, "watch");
            Directory.CreateDirectory(Watch);

            var options = new GuardOptions { EnableQuarantine = true, QuarantineKeep = 5 };
            Log = new InterceptLog(Path.Combine(Root, "intercept.log"), 20);
            Quarantine = new QuarantineStore(Path.Combine(Root, "quarantine"));
            Runtime = new GuardRuntime(options, Quarantine, Log);
        }

        public string Root { get; }
        public string Watch { get; }
        public InterceptLog Log { get; }
        public QuarantineStore Quarantine { get; }
        public GuardRuntime Runtime { get; }

        public GuardEntry WatchEntry(long maxBytes, bool limit = true) => new()
        {
            RootPath = Watch,
            MaxBytes = maxBytes,
            LimitEnabled = limit,
            WatchWrite = true,
            WatchCreate = true,
        };

        public void Start(GuardEntry entry)
        {
            Runtime.Apply(new[] { entry });
            Runtime.Start();
        }

        public void Dispose()
        {
            Runtime.Dispose();
            Cleanup(Root);
        }
    }
}
