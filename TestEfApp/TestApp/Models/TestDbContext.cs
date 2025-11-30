using Microsoft.EntityFrameworkCore;

namespace TestApp.Models
{
    public class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<CheckList> CheckLists { get; set; }
    }

    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
    }

    public class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
    }

    public class Order
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public DateTime OrderDate { get; set; }
        public User User { get; set; } = null!;
    }

    public class CheckList
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Familly { get; set; } = "";
    }
}