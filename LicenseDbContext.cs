using Microsoft.EntityFrameworkCore;

public class LicenseDbContext : DbContext
{
    public LicenseDbContext(DbContextOptions<LicenseDbContext> options) : base(options) { }

    public DbSet<LicenseKey> LicenseKeys { get; set; }
}
