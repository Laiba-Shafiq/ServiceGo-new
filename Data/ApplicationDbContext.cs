using Microsoft.EntityFrameworkCore;
using ServiceGo.Models;

namespace ServiceGo.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ProviderSignup> ProviderSignup { get; set; }
    public DbSet<Register> Register { get; set; }
    public DbSet<CustomerSignup> CustomerSignup { get; set; }
    public DbSet<Admin> Admins { get; set; }
    public DbSet<Booking> Booking { get; set; }
    public DbSet<Message> Messages { get; set; }
    public DbSet<ComplaintModel> Complaints { get; set; }
    public DbSet<WalletRequest> WalletRequests { get; set; }
    public DbSet<Notification> Notifications { get; set; }
}
