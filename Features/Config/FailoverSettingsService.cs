using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;

namespace SeedForge.Features.Config
{
    /// <summary>
    /// Read/write access to the single global <see cref="FailoverSetting"/> row (Id == 1) for the <c>/config</c> page.
    /// Lazily creates the row on first read so the app never needs a seeding step for it. The failover decorator reads
    /// the same row directly (it lives in the Services layer and must not depend on Features).
    /// </summary>
    public sealed class FailoverSettingsService(ApplicationDbContext db)
    {
        /// <summary>The current setting, creating a disabled default row on first access.</summary>
        public async Task<FailoverSetting> GetAsync(CancellationToken ct = default)
        {
            var setting = await db.FailoverSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
            if (setting is null)
            {
                setting = new FailoverSetting { Id = 1, Enabled = false, FallbackProfileId = null };
                db.FailoverSettings.Add(setting);
                await db.SaveChangesAsync(ct);
            }
            return setting;
        }

        /// <summary>Persists the enabled flag and chosen fallback profile.</summary>
        public async Task UpdateAsync(bool enabled, int? fallbackProfileId, CancellationToken ct = default)
        {
            var setting = await GetAsync(ct);
            setting.Enabled = enabled;
            setting.FallbackProfileId = fallbackProfileId;
            await db.SaveChangesAsync(ct);
        }
    }
}
