using FileGuard.Service;

namespace FileGuard.Service.Tests;

internal sealed class FakeServiceLog : IServiceLog
{
    public List<string> Infos { get; } = new();

    public List<string> Errors { get; } = new();

    public void Info(string message) => Infos.Add(message);

    public void Error(string message, Exception? exception = null) =>
        Errors.Add(exception is null ? message : $"{message} | {exception.GetType().Name}");
}

internal sealed class FakeAdminChecker : IClientAdminChecker
{
    public bool Result { get; set; }

    public uint LastClientPid { get; private set; }

    public int Calls { get; private set; }

    public bool IsClientAdmin(uint clientProcessId)
    {
        Calls++;
        LastClientPid = clientProcessId;
        return Result;
    }
}

internal sealed class FakeProcessProbe : IAppProcessProbe
{
    public int? Pid { get; set; }

    public int Calls { get; private set; }

    public int? FindRunningAppPid()
    {
        Calls++;
        return Pid;
    }
}

internal sealed class FakeLauncher : IAppLauncher
{
    private readonly Queue<LaunchResult> _results = new();

    public int Calls { get; private set; }

    public void Enqueue(LaunchResult result) => _results.Enqueue(result);

    public LaunchResult TryLaunch()
    {
        Calls++;
        return _results.Count > 0 ? _results.Dequeue() : LaunchResult.Ok();
    }
}
