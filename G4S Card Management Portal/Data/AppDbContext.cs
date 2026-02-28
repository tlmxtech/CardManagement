using Microsoft.EntityFrameworkCore;
using CardManagement.Models;

namespace CardManagement.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<Reseller> Resellers { get; set; }
        public DbSet<Company> Companies { get; set; }
        public DbSet<Unit> Units { get; set; }
        public DbSet<Device> Devices { get; set; }
        public DbSet<Card> Cards { get; set; }
        public DbSet<DeviceCard> DeviceCards { get; set; }
        public DbSet<Command> Commands { get; set; }
        public DbSet<PollJob> PollJobs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DeviceCard>()
                .HasKey(dc => new { dc.DeviceId, dc.CardId });

            modelBuilder.Entity<DeviceCard>()
                .HasOne(dc => dc.Device)
                .WithMany(d => d.DeviceCards)
                .HasForeignKey(dc => dc.DeviceId);

            modelBuilder.Entity<DeviceCard>()
                .HasOne(dc => dc.Card)
                .WithMany(c => c.DeviceCards)
                .HasForeignKey(dc => dc.CardId);

            modelBuilder.Entity<PollJob>()
                .HasOne(p => p.Device)
                .WithMany(d => d.PollJobs)
                .HasForeignKey(p => p.DeviceId);
        }
    }
}