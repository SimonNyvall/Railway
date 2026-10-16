using Railway.Engine.Domain;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Orchestration;

public sealed class RunHistoryQuery : IRunHistoryQuery
{
    private readonly IRunHistoryService _runHistoryService;

    public RunHistoryQuery(IRunHistoryService runHistoryService)
    {
        _runHistoryService = runHistoryService;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Run>> ListRunsAsync(string startPath, int limit = 50, CancellationToken ct = default)
    {
        return await _runHistoryService.ListRunsAsync(startPath, limit, ct);
    }

    /// <inheritdoc/>
    public async Task<Run> GetRunAsync(string startPath, int runId, CancellationToken ct = default)
    {
        return await _runHistoryService.GetRunAsync(startPath, runId, ct);
    }
}