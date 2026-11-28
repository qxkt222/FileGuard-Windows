using System.Text;

namespace FileGuard.SelfCheck;

internal enum Status
{
    Pass,
    Fail,
    Skip,
}

internal sealed record CheckResult(string Category, string Name, Status Status, string? Detail);

/// <summary>一项检查。返回 null 表示通过，否则返回失败原因。</summary>
internal delegate CheckResult CheckBody(string category, string name);

internal sealed class CheckRegistry
{
    private readonly List<(string Category, string Name, CheckBody Body)> _checks = new();

    public IReadOnlyList<(string Category, string Name, CheckBody Body)> Checks => _checks;

    public void Add(string category, string name, CheckBody body)
        => _checks.Add((category, name, body));

    /// <summary>布尔断言。</summary>
    public void That(string category, string name, Func<bool> predicate, string failDetail = "断言为 false")
        => Add(category, name, (c, n) =>
        {
            bool ok;
            try
            {
                ok = predicate();
            }
            catch (Exception ex)
            {
                return new CheckResult(c, n, Status.Fail, $"抛异常 {ex.GetType().Name}: {ex.Message}");
            }

            return ok
                ? new CheckResult(c, n, Status.Pass, null)
                : new CheckResult(c, n, Status.Fail, failDetail);
        });

    /// <summary>相等断言，失败时打印期望值与实际值。</summary>
    public void Equal<T>(string category, string name, T actual, T expected)
        => That(category, name,
            () => EqualityComparer<T>.Default.Equals(actual, expected),
            $"期望 {Format(expected)}，实际 {Format(actual)}");

    /// <summary>执行一段代码，不抛异常即通过。</summary>
    public void Runs(string category, string name, Action body)
        => Add(category, name, (c, n) =>
        {
            try
            {
                body();
                return new CheckResult(c, n, Status.Pass, null);
            }
            catch (Exception ex)
            {
                return new CheckResult(c, n, Status.Fail, $"抛异常 {ex.GetType().Name}: {ex.Message}");
            }
        });

    /// <summary>期望抛出指定异常（含派生类）。</summary>
    public void Throws<TException>(string category, string name, Action body)
        where TException : Exception
        => Add(category, name, (c, n) =>
        {
            try
            {
                body();
                return new CheckResult(c, n, Status.Fail, $"没有抛异常，期望 {typeof(TException).Name}");
            }
            catch (TException)
            {
                return new CheckResult(c, n, Status.Pass, null);
            }
            catch (Exception ex)
            {
                return new CheckResult(c, n, Status.Fail,
                    $"抛了 {ex.GetType().Name}，期望 {typeof(TException).Name}: {ex.Message}");
            }
        });

    /// <summary>依赖管理员 / 服务 / 驱动的检查，记录跳过原因。</summary>
    public void Skip(string category, string name, string reason)
        => Add(category, name, (c, n) => new CheckResult(c, n, Status.Skip, reason));

    private static string Format<T>(T value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? "null",
    };
}

internal static class Runner
{
    public static int Run(CheckRegistry registry, bool verbose)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var results = new List<CheckResult>();
        var currentCategory = string.Empty;

        Console.WriteLine($"文件卫士自检 · {registry.Checks.Count} 项");
        Console.WriteLine(new string('=', 60));

        foreach (var (category, name, body) in registry.Checks)
        {
            if (category != currentCategory)
            {
                currentCategory = category;
                Console.WriteLine();
                Console.WriteLine($"[{category}]");
            }

            CheckResult result;
            try
            {
                result = body(category, name);
            }
            catch (Exception ex)
            {
                result = new CheckResult(category, name, Status.Fail,
                    $"检查本身崩溃 {ex.GetType().Name}: {ex.Message}");
            }

            results.Add(result);
            Console.WriteLine("  " + Render(result, verbose));
        }

        var pass = results.Count(r => r.Status == Status.Pass);
        var fail = results.Count(r => r.Status == Status.Fail);
        var skip = results.Count(r => r.Status == Status.Skip);

        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"结果：通过 {pass} / 失败 {fail} / 跳过 {skip}，共 {results.Count} 项");

        if (fail > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败明细：");
            foreach (var r in results.Where(r => r.Status == Status.Fail))
                Console.WriteLine($"  ✗ [{r.Category}] {r.Name} — {r.Detail}");
        }

        return fail == 0 ? 0 : 1;
    }

    private static string Render(CheckResult result, bool verbose) => result.Status switch
    {
        Status.Pass => verbose && result.Detail is not null
            ? $"✓ {result.Name} — {result.Detail}"
            : $"✓ {result.Name}",
        Status.Fail => $"✗ {result.Name} — {result.Detail}",
        Status.Skip => $"○ {result.Name} — 跳过：{result.Detail}",
        _ => $"  {result.Name}",
    };
}
