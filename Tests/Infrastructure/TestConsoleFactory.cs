using Spectre.Console;
using Spectre.Console.Testing;

namespace Tests.Infrastructure;

/// <summary>
/// Creates a <see cref="TestConsole"/> and assigns it to the static
/// <see cref="AnsiConsole.Console"/>, since handlers and commands write to
/// the static AnsiConsole rather than an injected instance.
/// </summary>
public static class TestConsoleFactory
{
    public static TestConsole Create()
    {
        var console = new TestConsole();
        AnsiConsole.Console = console;
        return console;
    }
}
