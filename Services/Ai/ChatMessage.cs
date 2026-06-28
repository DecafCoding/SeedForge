namespace SeedForge.Services.Ai
{
    /// <summary>One chat message (role + content) sent to an OpenAI-compatible endpoint.</summary>
    public sealed record ChatMessage(string Role, string Content);
}
