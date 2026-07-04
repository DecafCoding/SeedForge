namespace SeedForge.Services.Theme;

/// <summary>
/// Reads and persists the user's chosen UI theme ("light" / "dark").
/// </summary>
public interface IThemeService
{
    Task<string> GetThemeAsync();
    Task SetThemeAsync(string theme);
    event Action<string>? ThemeChanged;
}
