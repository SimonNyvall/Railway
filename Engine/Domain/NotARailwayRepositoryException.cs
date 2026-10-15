namespace Railway.Engine.Domain;

public sealed class NotARailwayRepositoryException : Exception
{
    public const string FatalMessage = "fatal: not a railway repository (or any parent): .railway";
    public const string HintMessage = "hint: run 'railway init'";

    public NotARailwayRepositoryException()
        : base($"{FatalMessage}{Environment.NewLine}{HintMessage}")
    {
    }
}
