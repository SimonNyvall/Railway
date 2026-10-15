namespace Railway.Engine.Domain;

public sealed class NotAGitRepositoryException : Exception
{
    public const string FatalMessage = "fatal: not a git repository (or any of the parent directories): .git";

    public NotAGitRepositoryException()
        : base(FatalMessage)
    {
    }
}
