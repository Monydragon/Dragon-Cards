using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DragonCards.Persistence;

/// <summary>Creates SQLite contexts for the desktop client and EF migration tooling.</summary>
public static class DragonCardsDbContextFactory
{
    public static DragonCardsDbContext Create(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var builder = new DbContextOptionsBuilder<DragonCardsDbContext>();
        builder.UseSqlite($"Data Source={databasePath};Foreign Keys=True");
        return new DragonCardsDbContext(builder.Options);
    }
}

public sealed class DragonCardsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<DragonCardsDbContext>
{
    public DragonCardsDbContext CreateDbContext(string[] args)
    {
        var databasePath = Environment.GetEnvironmentVariable("DRAGON_CARDS_DB")
            ?? Path.Combine(Path.GetTempPath(), "DragonCards", "dragoncards-design.db");
        return DragonCardsDbContextFactory.Create(databasePath);
    }
}
