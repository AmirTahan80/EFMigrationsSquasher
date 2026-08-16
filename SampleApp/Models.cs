using Microsoft.EntityFrameworkCore;

namespace SampleApp
{
    public class SampleDbContext : DbContext
    {
        public SampleDbContext(DbContextOptions<SampleDbContext> options) : base(options) { }

        public DbSet<Razi> Razis { get; set; } = null!;
        public DbSet<Other> Others { get; set; } = null!;
    }

    // Final shape: table "Razi" (renamed from Rozi in migration 2), column Note added in migration 3.
    public class Razi
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Note { get; set; }
    }

    public class Other
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }
}
