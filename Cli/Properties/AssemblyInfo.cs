using System.Runtime.CompilerServices;

// Allows the Tests project to exercise internal pure helpers on production services
// (e.g. RunnerService.TopologicalSort/BuildEnvironment/MergeEnvironments) directly,
// instead of re-implementing their logic in the test suite.
[assembly: InternalsVisibleTo("Tests")]
