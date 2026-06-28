using Microsoft.Extensions.Options;

namespace SeedForge.UnitTests
{
    /// <summary>Minimal <see cref="IOptionsMonitor{T}"/> over a fixed value, for resolver/logger tests.</summary>
    public sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
