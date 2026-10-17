namespace Railway.Engine.Domain;

public enum RunStatus
{
    Succeeded,
    Failed,
    Running,
    Cancelled,
    // Skipped must stay at the END so persisted numeric values never shift
    // (Succeeded = 0 is the uninitialized default — see AGENTS.md gotchas).
    Skipped
}
