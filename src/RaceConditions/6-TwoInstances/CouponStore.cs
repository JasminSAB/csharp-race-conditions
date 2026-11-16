using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace RaceConditions.TwoInstances;

public sealed class Redemption
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Customer { get; set; } = string.Empty;
}

/// <summary>The shared database every instance of the API talks to. Code is indexed, not unique.</summary>
public class CouponDb(DbContextOptions options) : DbContext(options)
{
    public DbSet<Redemption> Redemptions => Set<Redemption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Redemption>().HasIndex(r => r.Code);

    public static CouponDb Open(string databaseFile) => new(Options(databaseFile));

    protected static DbContextOptions Options(string databaseFile) =>
        new DbContextOptionsBuilder().UseSqlite($"Data Source={databaseFile}").Options;
}

/// <summary>Fix: the database itself refuses a second redemption of the same code.</summary>
public sealed class CouponDbWithUniqueCode(DbContextOptions options) : CouponDb(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Redemption>().HasIndex(r => r.Code).IsUnique();

    public static new CouponDbWithUniqueCode Open(string databaseFile) => new(Options(databaseFile));
}

/// <summary>
/// One instance of the API. The in-process claim from the check-then-act fix is correct inside one
/// process, but every instance has its own dictionary, and the "already redeemed?" query and the
/// insert are two steps. Two instances behind a load balancer both pass the check and both write.
/// </summary>
public sealed class CouponService(Func<CouponDb> openDb, Func<Task>? betweenCheckAndWrite = null)
{
    private readonly ConcurrentDictionary<string, string> _claims = new();

    public async Task<bool> RedeemAsync(string code, string customer)
    {
        if (!_claims.TryAdd(code, customer)) return false;

        await using var db = openDb();
        if (await db.Redemptions.AnyAsync(r => r.Code == code)) return false;

        if (betweenCheckAndWrite is not null) await betweenCheckAndWrite();

        db.Redemptions.Add(new Redemption { Code = code, Customer = customer });
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            // The unique index rejected the second redemption: another instance won.
            return false;
        }
    }
}
