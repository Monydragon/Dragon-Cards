using Microsoft.EntityFrameworkCore;

namespace DragonCards.Persistence;

public sealed class DragonCardsDbContext(DbContextOptions<DragonCardsDbContext> options) : DbContext(options)
{
    public DbSet<AppSettingEntity> AppSettings => Set<AppSettingEntity>();
    public DbSet<ProfileEntity> Profiles => Set<ProfileEntity>();
    public DbSet<CardCopyEntity> CardCopies => Set<CardCopyEntity>();
    public DbSet<PackInventoryEntity> PackInventory => Set<PackInventoryEntity>();
    public DbSet<StarterDeckOwnershipEntity> StarterDeckOwnership => Set<StarterDeckOwnershipEntity>();
    public DbSet<TutorialCompletionEntity> TutorialCompletions => Set<TutorialCompletionEntity>();
    public DbSet<QuestStateEntity> QuestStates => Set<QuestStateEntity>();
    public DbSet<QuestEntryEntity> QuestEntries => Set<QuestEntryEntity>();
    public DbSet<DeckEntity> Decks => Set<DeckEntity>();
    public DbSet<DeckCardEntity> DeckCards => Set<DeckCardEntity>();
    public DbSet<ProfileEventEntity> ProfileEvents => Set<ProfileEventEntity>();
    public DbSet<ProfileSeedRunEntity> ProfileSeedRuns => Set<ProfileSeedRunEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSettingEntity>(entity =>
        {
            entity.ToTable("AppSettings");
            entity.HasKey(item => item.Key);
            entity.Property(item => item.Key).HasMaxLength(80);
        });

        modelBuilder.Entity<ProfileEntity>(entity =>
        {
            entity.ToTable("Profiles");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasMaxLength(32);
            entity.Property(item => item.DisplayName).HasMaxLength(18).UseCollation("NOCASE");
            entity.HasIndex(item => item.DisplayName).IsUnique();
        });

        modelBuilder.Entity<CardCopyEntity>(entity =>
        {
            entity.ToTable("CardCopies");
            entity.HasKey(item => new { item.ProfileId, item.CardId });
            entity.Property(item => item.ProfileId).HasMaxLength(32);
            entity.Property(item => item.CardId).HasMaxLength(160);
            entity.HasOne<ProfileEntity>().WithMany().HasForeignKey(item => item.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PackInventoryEntity>(entity =>
        {
            entity.ToTable("PackInventory");
            entity.HasKey(item => new { item.ProfileId, item.PackId });
            entity.Property(item => item.ProfileId).HasMaxLength(32);
            entity.Property(item => item.PackId).HasMaxLength(160);
            entity.HasOne<ProfileEntity>().WithMany().HasForeignKey(item => item.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StarterDeckOwnershipEntity>(entity =>
        {
            entity.ToTable("StarterDeckOwnership");
            entity.HasKey(item => new { item.ProfileId, item.StarterDeckId });
            entity.Property(item => item.ProfileId).HasMaxLength(32);
            entity.Property(item => item.StarterDeckId).HasMaxLength(160);
            entity.HasOne<ProfileEntity>().WithMany().HasForeignKey(item => item.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TutorialCompletionEntity>(entity =>
        {
            entity.ToTable("TutorialCompletions");
            entity.HasKey(item => new { item.ProfileId, item.TutorialId });
            entity.Property(item => item.ProfileId).HasMaxLength(32);
            entity.Property(item => item.TutorialId).HasMaxLength(160);
            entity.HasOne<ProfileEntity>().WithMany().HasForeignKey(item => item.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuestStateEntity>(entity =>
        {
            entity.ToTable("QuestStates");
            entity.HasKey(item => item.ProfileId);
            entity.Property(item => item.ProfileId).HasMaxLength(32);
            entity.HasOne<ProfileEntity>().WithMany().HasForeignKey(item => item.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuestEntryEntity>(entity =>
        {
            entity.ToTable("QuestEntries");
            entity.HasKey(item => new { item.ProfileId, item.QuestId });
            entity.Property(item => item.ProfileId).HasMaxLength(32);
            entity.Property(item => item.QuestId).HasMaxLength(160);
            entity.HasOne<ProfileEntity>().WithMany().HasForeignKey(item => item.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeckEntity>(entity =>
        {
            entity.ToTable("Decks");
            entity.HasKey(item => new { item.ProfileId, item.DeckId });
            entity.Property(item => item.ProfileId).HasMaxLength(32);
            entity.Property(item => item.DeckId).HasMaxLength(128);
            entity.Property(item => item.Name).HasMaxLength(80);
            entity.Property(item => item.ModeId).HasMaxLength(80);
            entity.HasOne<ProfileEntity>().WithMany().HasForeignKey(item => item.ProfileId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(item => new { item.ProfileId, item.Name });
        });

        modelBuilder.Entity<DeckCardEntity>(entity =>
        {
            entity.ToTable("DeckCards");
            entity.HasKey(item => new { item.ProfileId, item.DeckId, item.CardId });
            entity.Property(item => item.ProfileId).HasMaxLength(32);
            entity.Property(item => item.DeckId).HasMaxLength(128);
            entity.Property(item => item.CardId).HasMaxLength(160);
            entity.HasOne<DeckEntity>().WithMany().HasForeignKey(item => new { item.ProfileId, item.DeckId }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileEventEntity>(entity =>
        {
            entity.ToTable("ProfileEvents");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasMaxLength(32);
            entity.Property(item => item.ProfileId).HasMaxLength(32);
            entity.Property(item => item.Kind).HasMaxLength(80);
            entity.Property(item => item.Summary).HasMaxLength(512);
            entity.HasOne<ProfileEntity>().WithMany().HasForeignKey(item => item.ProfileId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(item => new { item.ProfileId, item.OccurredUnixMilliseconds });
        });

        modelBuilder.Entity<ProfileSeedRunEntity>(entity =>
        {
            entity.ToTable("ProfileSeedRuns");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasMaxLength(32);
            entity.Property(item => item.ProfileId).HasMaxLength(32);
            entity.Property(item => item.AlgorithmVersion).HasMaxLength(40);
            entity.Property(item => item.Scenario).HasMaxLength(80);
            entity.Property(item => item.Summary).HasMaxLength(512);
            entity.HasOne<ProfileEntity>().WithMany().HasForeignKey(item => item.ProfileId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(item => new { item.ProfileId, item.AppliedUnixMilliseconds });
        });
    }

}
