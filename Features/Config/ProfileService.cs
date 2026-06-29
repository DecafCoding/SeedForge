using Microsoft.EntityFrameworkCore;
using SeedForge.Data;
using SeedForge.Domain;

namespace SeedForge.Features.Config
{
    /// <summary>
    /// Profile CRUD + set-active for <see cref="ConfigProfile"/>. <see cref="SetActiveAsync"/> keeps exactly one
    /// profile active (satisfying the filtered unique index) by flipping the prior active off in the same save.
    /// </summary>
    public sealed class ProfileService(ApplicationDbContext db)
    {
        /// <summary>All profiles, newest first.</summary>
        public Task<List<ConfigProfile>> ListAsync(CancellationToken ct = default) =>
            db.ConfigProfiles.OrderByDescending(p => p.Id).ToListAsync(ct);

        /// <summary>Makes <paramref name="id"/> the sole active profile (turning any prior active off).</summary>
        /// <exception cref="InvalidOperationException">Thrown when the profile does not exist.</exception>
        public async Task SetActiveAsync(int id, CancellationToken ct = default)
        {
            var target = await db.ConfigProfiles.FindAsync([id], ct)
                ?? throw new InvalidOperationException($"ConfigProfile {id} not found");

            // Turn the prior active off FIRST (separate save): the filtered unique index on IsActive rejects a
            // transient two-active state, which a single batch could hit if EF orders the "set true" update first.
            var currentlyActive = await db.ConfigProfiles.Where(p => p.IsActive && p.Id != id).ToListAsync(ct);
            if (currentlyActive.Count > 0)
            {
                foreach (var p in currentlyActive) p.IsActive = false;
                await db.SaveChangesAsync(ct);
            }

            target.IsActive = true;
            await db.SaveChangesAsync(ct);
        }

        /// <summary>Inserts or updates a profile.</summary>
        public async Task SaveAsync(ConfigProfile profile, CancellationToken ct = default)
        {
            if (profile.Id == 0)
            {
                profile.CreatedAtUtc = DateTime.UtcNow;
                db.ConfigProfiles.Add(profile);
            }
            else
            {
                db.ConfigProfiles.Update(profile);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
