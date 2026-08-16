using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SampleApp
{
    // Enables design-time commands (dotnet ef migrations script / database update)
    // without a full host. Uses a local SQL Server; adjust the connection string as needed.
    public class SampleDbContextFactory : IDesignTimeDbContextFactory<SampleDbContext>
    {
        public SampleDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<SampleDbContext>();
            optionsBuilder.UseSqlServer(
                "Server=(localdb)\\mssqllocaldb;Database=SampleApp;Trusted_Connection=True;");
            return new SampleDbContext(optionsBuilder.Options);
        }
    }
}
