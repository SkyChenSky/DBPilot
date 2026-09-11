namespace DBPilot.Scenarios;

interface IJob
{
    string Key { get; }
    string Title { get; }
    Task RunAsync(CancellationToken ct = default);
}
