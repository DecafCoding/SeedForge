using System.Runtime.CompilerServices;

// Exposes the LlmClient internal raw overloads (usage + raw body) to the unit-test project.
[assembly: InternalsVisibleTo("SeedForge.UnitTests")]
