using System.Runtime.CompilerServices;

// Allows the Tests project to exercise internal MCP helpers directly
// (e.g. RunEventProgressAdapter) instead of re-implementing their logic.
[assembly: InternalsVisibleTo("Tests")]
