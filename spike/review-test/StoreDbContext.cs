using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Shop;

public class StoreDbContext(DbContextOptions<StoreDbContext> options, ICurrentShop currentShop, IFieldEncryptor encryptor) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<TicketNote> TicketNotes => Set<TicketNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureCustomers(modelBuilder);
        ConfigureCatalog(modelBuilder);
        ConfigureOrders(modelBuilder);
        ConfigurePayments(modelBuilder);
        ConfigureSupport(modelBuilder);
    }

    void ScopeToShop<T>(ModelBuilder modelBuilder) where T : class, IShopScoped =>
        modelBuilder.Entity<T>().HasQueryFilter(e => currentShop.Ids.Contains(e.ShopId));

    void ConfigureCustomers(ModelBuilder modelBuilder)
    {
        // Encrypted at rest. Deterministic, so the unique index and equality lookups still work.
        var encrypted = new ValueConverter<string, string>(v => encryptor.Encrypt(v), v => encryptor.Decrypt(v));

        modelBuilder.Entity<Customer>().HasIndex(c => c.Email).IsUnique();
        modelBuilder.Entity<Customer>().Property(c => c.Email).HasMaxLength(320);
        modelBuilder.Entity<Customer>().Property(c => c.FullName).HasMaxLength(200);
        modelBuilder.Entity<Customer>().Property(c => c.PasswordHash).HasMaxLength(500);
        modelBuilder.Entity<Customer>().Property(c => c.TaxNumber).HasMaxLength(256).HasConversion(encrypted);
        modelBuilder.Entity<Customer>().HasIndex(c => c.TaxNumber).IsUnique();
        modelBuilder.Entity<Customer>().HasAlternateKey(c => new { c.Id, c.ShopId });
        modelBuilder.Entity<Customer>().Property<uint>("Version").IsRowVersion();
        ScopeToShop<Customer>(modelBuilder);

        modelBuilder.Entity<Address>()
            .HasOne<Customer>().WithMany()
            .HasForeignKey(a => new { a.CustomerId, a.ShopId })
            .HasPrincipalKey(c => new { c.Id, c.ShopId })
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Address>().Property(a => a.Line1).HasMaxLength(300);
        modelBuilder.Entity<Address>().Property(a => a.City).HasMaxLength(150);
        modelBuilder.Entity<Address>().Property(a => a.PostCode).HasMaxLength(20);
        modelBuilder.Entity<Address>().HasIndex(a => a.CustomerId);
        ScopeToShop<Address>(modelBuilder);

        modelBuilder.Entity<PasswordResetToken>()
            .HasOne(t => t.Customer).WithMany()
            .HasForeignKey(t => t.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PasswordResetToken>().HasIndex(t => t.TokenHash).IsUnique();
        modelBuilder.Entity<PasswordResetToken>().Property(t => t.TokenHash).HasMaxLength(64);
        modelBuilder.Entity<PasswordResetToken>().HasIndex(t => new { t.CustomerId, t.ExpiresAt });
    }

    void ConfigureCatalog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>().Property(c => c.Name).HasMaxLength(100);
        modelBuilder.Entity<Category>().HasIndex(c => new { c.ShopId, c.Name }).IsUnique();
        modelBuilder.Entity<Category>().HasAlternateKey(c => new { c.Id, c.ShopId });
        ScopeToShop<Category>(modelBuilder);

        modelBuilder.Entity<Product>()
            .HasOne<Category>().WithMany()
            .HasForeignKey(p => new { p.CategoryId, p.ShopId })
            .HasPrincipalKey(c => new { c.Id, c.ShopId })
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Product>().Property(p => p.Name).HasMaxLength(200);
        modelBuilder.Entity<Product>().Property(p => p.Description).HasMaxLength(4000);
        modelBuilder.Entity<Product>().Property(p => p.Price).HasPrecision(12, 2);
        modelBuilder.Entity<Product>().HasIndex(p => new { p.ShopId, p.Sku }).IsUnique();
        modelBuilder.Entity<Product>().HasAlternateKey(p => new { p.Id, p.ShopId });
        modelBuilder.Entity<Product>().ToTable(t => t.HasCheckConstraint("CK_Products_Price", "\"Price\" >= 0"));
        modelBuilder.Entity<Product>().Property<uint>("Version").IsRowVersion();
        ScopeToShop<Product>(modelBuilder);
    }

    void ConfigureOrders(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>()
            .HasOne<Customer>().WithMany()
            .HasForeignKey(o => new { o.CustomerId, o.ShopId })
            .HasPrincipalKey(c => new { c.Id, c.ShopId })
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Order>().Property(o => o.Total).HasPrecision(12, 2);
        modelBuilder.Entity<Order>().HasIndex(o => new { o.ShopId, o.PlacedAt });
        modelBuilder.Entity<Order>().HasAlternateKey(o => new { o.Id, o.ShopId });
        modelBuilder.Entity<Order>().Property<uint>("Version").IsRowVersion();
        ScopeToShop<Order>(modelBuilder);

        modelBuilder.Entity<OrderLine>()
            .HasOne<Order>().WithMany(o => o.Lines)
            .HasForeignKey(l => new { l.OrderId, l.ShopId })
            .HasPrincipalKey(o => new { o.Id, o.ShopId })
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OrderLine>()
            .HasOne<Product>().WithMany()
            .HasForeignKey(l => new { l.ProductId, l.ShopId })
            .HasPrincipalKey(p => new { p.Id, p.ShopId })
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<OrderLine>().Property(l => l.UnitPrice).HasPrecision(12, 2);
        modelBuilder.Entity<OrderLine>().HasIndex(l => new { l.OrderId, l.ProductId }).IsUnique();
        modelBuilder.Entity<OrderLine>().ToTable(t => t.HasCheckConstraint("CK_OrderLines_Quantity", "\"Quantity\" > 0"));
        ScopeToShop<OrderLine>(modelBuilder);
    }

    void ConfigurePayments(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>()
            .HasOne<Order>().WithMany()
            .HasForeignKey(p => new { p.OrderId, p.ShopId })
            .HasPrincipalKey(o => new { o.Id, o.ShopId })
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Payment>().Property(p => p.Amount).HasPrecision(12, 2);
        modelBuilder.Entity<Payment>().Property(p => p.ProviderReference).HasMaxLength(200);
        modelBuilder.Entity<Payment>().HasIndex(p => p.ProviderReference).IsUnique();
        modelBuilder.Entity<Payment>().Property<uint>("Version").IsRowVersion();
        ScopeToShop<Payment>(modelBuilder);

        modelBuilder.Entity<Refund>()
            .HasOne<Payment>().WithMany()
            .HasForeignKey(r => r.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Refund>().Property(r => r.Amount).HasPrecision(12, 2);
        modelBuilder.Entity<Refund>().Property(r => r.Reason).HasMaxLength(500);
        modelBuilder.Entity<Refund>().ToTable(t => t.HasCheckConstraint("CK_Refunds_Amount", "\"Amount\" > 0"));
        ScopeToShop<Refund>(modelBuilder);
    }

    void ConfigureSupport(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SupportTicket>()
            .HasOne<Customer>().WithMany()
            .HasForeignKey(t => new { t.CustomerId, t.ShopId })
            .HasPrincipalKey(c => new { c.Id, c.ShopId })
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SupportTicket>().Property(t => t.Subject).HasMaxLength(200);
        modelBuilder.Entity<SupportTicket>().HasIndex(t => new { t.ShopId, t.OpenedAt });
        modelBuilder.Entity<SupportTicket>().Property<uint>("Version").IsRowVersion();
        ScopeToShop<SupportTicket>(modelBuilder);

        modelBuilder.Entity<TicketNote>()
            .HasOne<SupportTicket>().WithMany()
            .HasForeignKey(n => n.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TicketNote>().Property(n => n.Body).HasMaxLength(8000);
        modelBuilder.Entity<TicketNote>().HasIndex(n => new { n.TicketId, n.WrittenAt });
        ScopeToShop<TicketNote>(modelBuilder);
    }
}
