using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SNIF.Core.Entities;
using SNIF.Core.Enums;
using SNIF.Core.Models;

namespace SNIF.Infrastructure.Data
{
    public class SNIFContext : IdentityDbContext<User>
    {
        public SNIFContext(DbContextOptions<SNIFContext> options) : base(options) { }

        public DbSet<Pet> Pets => Set<Pet>();
        public DbSet<AnimalBreed> AnimalBreeds => Set<AnimalBreed>();
        public DbSet<BreederVerification> BreederVerifications => Set<BreederVerification>();
        public DbSet<UserPreferences> UserPreferences => Set<UserPreferences>();
        public DbSet<DiscoveryPreferences> DiscoveryPreferences => Set<DiscoveryPreferences>();
        public DbSet<Location> Locations => Set<Location>();
        public DbSet<Match> Matches => Set<Match>();
        public DbSet<Message> Messages => Set<Message>();
        public DbSet<PetMedia> PetMedia => Set<PetMedia>();
        public DbSet<SwipeAction> SwipeActions => Set<SwipeAction>();
        public DbSet<Subscription> Subscriptions => Set<Subscription>();
        public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
        public DbSet<CreditBalance> CreditBalances => Set<CreditBalance>();
        public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
        public DbSet<Report> Reports => Set<Report>();
        public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
        public DbSet<Notification> Notifications => Set<Notification>();
        public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
        public DbSet<VideoCall> VideoCalls => Set<VideoCall>();
        public DbSet<UserBoost> UserBoosts => Set<UserBoost>();
        public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<Location>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).ValueGeneratedOnAdd();
            });

            builder.Entity<User>()
                .HasIndex(u => u.GoogleSubjectId)
                .IsUnique()
                .HasFilter("\"GoogleSubjectId\" IS NOT NULL");

            builder.Entity<User>()
                .HasOne(u => u.Location)
                .WithMany()
                .HasForeignKey("LocationId")
                .OnDelete(DeleteBehavior.SetNull);

            builder.Entity<User>()
                .HasMany(u => u.Pets)
                .WithOne(p => p.Owner)
                .HasForeignKey(p => p.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<User>()
                .HasOne(u => u.BreederVerification)
                .WithOne(b => b.User)
                .HasForeignKey<BreederVerification>(b => b.UserId);

            builder.Entity<User>()
                .HasOne(u => u.Preferences);

            builder.Entity<Match>(b =>
            {
                b.HasKey(x => x.Id);

                // Configure InitiatorPet relationship
                b.HasOne(m => m.InitiatiorPet)
                 .WithMany(p => p.InitiatedMatches)
                 .HasForeignKey(m => m.InitiatiorPetId)
                 .OnDelete(DeleteBehavior.Restrict); // Prevent cascade delete

                // Configure TargetPet relationship
                b.HasOne(m => m.TargetPet)
                 .WithMany(p => p.ReceivedMatches)
                 .HasForeignKey(m => m.TargetPetId)
                 .OnDelete(DeleteBehavior.Restrict);
                b.Property(m => m.Status).HasConversion<string>();

                b.Property(m => m.Purpose)
                 .HasConversion<string>();
            });

            builder.Entity<Message>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(m => m.Sender)
                    .WithMany()
                    .HasForeignKey(m => m.SenderId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne(m => m.Receiver)
                    .WithMany()
                    .HasForeignKey(m => m.ReceiverId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne(m => m.Match)
                    .WithMany()
                    .HasForeignKey(m => m.MatchId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasMany(m => m.Reactions)
                    .WithOne(r => r.Message)
                    .HasForeignKey(r => r.MessageId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Filtered index for efficient lookup of undelivered messages per receiver
                b.HasIndex(m => new { m.ReceiverId, m.DeliveredAt })
                    .HasFilter("\"DeliveredAt\" IS NULL");
            });

            builder.Entity<MessageReaction>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(r => r.User)
                    .WithMany()
                    .HasForeignKey(r => r.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasIndex(r => new { r.MessageId, r.UserId, r.Emoji }).IsUnique();
            });

            builder.Entity<PetMedia>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).ValueGeneratedOnAdd();
                b.HasOne(m => m.Pet)
                    .WithMany(p => p.Media)
                    .HasForeignKey(m => m.PetId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<DiscoveryPreferences>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).ValueGeneratedOnAdd();
                b.HasOne(d => d.Pet)
                    .WithOne(p => p.DiscoveryPreferences)
                    .HasForeignKey<DiscoveryPreferences>(d => d.PetId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<SwipeAction>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(s => s.SwiperPet)
                    .WithMany()
                    .HasForeignKey(s => s.SwiperPetId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne(s => s.TargetPet)
                    .WithMany()
                    .HasForeignKey(s => s.TargetPetId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.Property(s => s.Direction).HasConversion<string>();

                b.HasIndex(s => new { s.SwiperPetId, s.TargetPetId }).IsUnique();
            });

            // Subscription
            builder.Entity<Subscription>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(s => s.User)
                    .WithMany()
                    .HasForeignKey(s => s.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.Property(s => s.PlanId).HasConversion<string>();
                b.Property(s => s.Status).HasConversion<string>();

                b.HasIndex(s => s.UserId);
                b.HasIndex(s => s.PaymentProviderSubscriptionId).IsUnique()
                    .HasFilter("\"PaymentProviderSubscriptionId\" IS NOT NULL");

                // Map renamed properties to existing DB columns (avoids migration)
                b.Property(s => s.PaymentProviderSubscriptionId).HasColumnName("StripeSubscriptionId");
                b.Property(s => s.PaymentProviderCustomerId).HasColumnName("StripeCustomerId");
            });

            // UsageRecord
            builder.Entity<UsageRecord>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(u => u.User)
                    .WithMany()
                    .HasForeignKey(u => u.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.Property(u => u.Type).HasConversion<string>();

                b.HasIndex(u => new { u.UserId, u.Type, u.Date });
            });

            // CreditBalance
            builder.Entity<CreditBalance>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(c => c.User)
                    .WithMany()
                    .HasForeignKey(c => c.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasIndex(c => c.UserId).IsUnique();
            });

            // UserBlock
            builder.Entity<UserBlock>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(ub => ub.BlockerUser)
                    .WithMany()
                    .HasForeignKey(ub => ub.BlockerUserId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne(ub => ub.BlockedUser)
                    .WithMany()
                    .HasForeignKey(ub => ub.BlockedUserId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasIndex(ub => new { ub.BlockerUserId, ub.BlockedUserId }).IsUnique();
            });

            // Report
            builder.Entity<Report>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(r => r.Reporter)
                    .WithMany()
                    .HasForeignKey(r => r.ReporterId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne(r => r.TargetUser)
                    .WithMany()
                    .HasForeignKey(r => r.TargetUserId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne(r => r.TargetPet)
                    .WithMany()
                    .HasForeignKey(r => r.TargetPetId)
                    .OnDelete(DeleteBehavior.SetNull);

                b.Property(r => r.Reason).HasConversion<string>();
                b.Property(r => r.Status).HasConversion<string>();

                b.HasIndex(r => r.ReporterId);
                b.HasIndex(r => r.TargetUserId);
                b.HasIndex(r => r.Status);
            });

            // DeviceToken
            builder.Entity<DeviceToken>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(d => d.User)
                    .WithMany()
                    .HasForeignKey(d => d.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasIndex(d => d.Token).IsUnique();
                b.HasIndex(d => d.UserId);
            });

            // Notification
            builder.Entity<Notification>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(n => n.User)
                    .WithMany()
                    .HasForeignKey(n => n.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasIndex(n => n.UserId);
                b.HasIndex(n => new { n.UserId, n.IsRead });
            });

            // VideoCall
            builder.Entity<VideoCall>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(v => v.Match)
                    .WithMany()
                    .HasForeignKey(v => v.MatchId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(v => v.Caller)
                    .WithMany()
                    .HasForeignKey(v => v.CallerUserId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne(v => v.Receiver)
                    .WithMany()
                    .HasForeignKey(v => v.ReceiverUserId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasIndex(v => v.MatchId);
            });

            // UserBoost
            builder.Entity<UserBoost>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(ub => ub.User)
                    .WithMany()
                    .HasForeignKey(ub => ub.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.Property(ub => ub.BoostType).HasConversion<string>();
                b.Property(ub => ub.Source).HasConversion<string>();

                b.HasIndex(ub => new { ub.UserId, ub.BoostType, ub.ExpiresAt });
                b.HasIndex(ub => ub.LemonSqueezyOrderId)
                    .IsUnique()
                    .HasFilter("\"LemonSqueezyOrderId\" IS NOT NULL");
            });
        }
    }
}