using AgendaApi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgendaApi.Infrastructure.Data;

public class AgendaDbContext : DbContext
{
    public AgendaDbContext(DbContextOptions<AgendaDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<CalendarConnection> CalendarConnections => Set<CalendarConnection>();
    public DbSet<ServiceType> ServiceTypes => Set<ServiceType>();
    public DbSet<AvailabilityRule> AvailabilityRules => Set<AvailabilityRule>();
    public DbSet<AvailabilityException> AvailabilityExceptions => Set<AvailabilityException>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Appointment> Appointments => Set<Appointment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // --- Tenant ---
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(e => e.IdTenant);
            entity.Property(e => e.Nombre).IsRequired().HasMaxLength(200);
            entity.Property(e => e.NombreComercial).HasMaxLength(200);
            entity.Property(e => e.Correo).HasMaxLength(150);
            entity.Property(e => e.Telefono).HasMaxLength(30);
            entity.Property(e => e.Direccion).HasMaxLength(250);
            entity.Property(e => e.AntelacionMinimaHoras).HasDefaultValue(0);
            entity.Property(e => e.AntelacionMaximaDias).HasDefaultValue(0);
            entity.Property(e => e.CalendarProvider).IsRequired().HasMaxLength(20).HasDefaultValue("google");
            entity.Property(e => e.Activo).HasDefaultValue(true);
            entity.Property(e => e.FechaCreacion).HasDefaultValueSql("GETUTCDATE()");
            entity.Property(e => e.FechaActualizacion).HasDefaultValueSql("GETUTCDATE()");

            entity.HasMany(e => e.ServiceTypes)
                  .WithOne(s => s.Tenant)
                  .HasForeignKey(s => s.IdTenant)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.AvailabilityRules)
                  .WithOne(a => a.Tenant)
                  .HasForeignKey(a => a.IdTenant)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Clients)
                  .WithOne(c => c.Tenant)
                  .HasForeignKey(c => c.IdTenant)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Appointments)
                  .WithOne(a => a.Tenant)
                  .HasForeignKey(a => a.IdTenant)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // --- CalendarConnection ---
        modelBuilder.Entity<CalendarConnection>(entity =>
        {
            entity.ToTable("calendar_connections");
            entity.HasKey(e => e.IdCalendarConnection);
            entity.Property(e => e.AccessTokenEncrypted).IsRequired();
            entity.Property(e => e.RefreshTokenEncrypted).IsRequired();
            entity.Property(e => e.AccountEmail).HasMaxLength(200);
            entity.Property(e => e.CalendarId).HasMaxLength(200);
            entity.Property(e => e.SyncChannelId).HasMaxLength(200);
            entity.Property(e => e.Activo).HasDefaultValue(true);
            entity.Property(e => e.FechaCreacion).HasDefaultValueSql("GETUTCDATE()");
            entity.Property(e => e.FechaActualizacion).HasDefaultValueSql("GETUTCDATE()");

            entity.HasOne(e => e.Tenant)
                  .WithOne(t => t.CalendarConnection)
                  .HasForeignKey<CalendarConnection>(e => e.IdTenant)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // --- ServiceType ---
        modelBuilder.Entity<ServiceType>(entity =>
        {
            entity.ToTable("service_types");
            entity.HasKey(e => e.IdServiceType);
            entity.Property(e => e.Nombre).IsRequired().HasMaxLength(150);
            entity.Property(e => e.Descripcion).HasMaxLength(500);
            entity.Property(e => e.DuracionMinutos).IsRequired();
            entity.Property(e => e.BufferMinutos).HasDefaultValue(0);
            entity.Property(e => e.CapacidadMaxima).HasDefaultValue(1);
            entity.Property(e => e.Precio).HasColumnType("decimal(10,2)");
            entity.Property(e => e.Activo).HasDefaultValue(true);
            entity.Property(e => e.FechaCreacion).HasDefaultValueSql("GETUTCDATE()");
        });

        // --- AvailabilityRule ---
        modelBuilder.Entity<AvailabilityRule>(entity =>
        {
            entity.ToTable("availability_rules");
            entity.HasKey(e => e.IdAvailabilityRule);
            entity.Property(e => e.DiaSemana).IsRequired();
            entity.Property(e => e.HoraInicio).IsRequired();
            entity.Property(e => e.HoraFin).IsRequired();
            entity.Property(e => e.Activo).HasDefaultValue(true);
            entity.Property(e => e.FechaCreacion).HasDefaultValueSql("GETUTCDATE()");
        });

        // --- AvailabilityException ---
        modelBuilder.Entity<AvailabilityException>(entity =>
        {
            entity.ToTable("availability_exceptions");
            entity.HasKey(e => e.IdAvailabilityException);
            entity.Property(e => e.Fecha).IsRequired();
            entity.Property(e => e.Motivo).HasMaxLength(200);
            entity.Property(e => e.FechaCreacion).HasDefaultValueSql("GETUTCDATE()");
            entity.HasIndex(e => new { e.IdTenant, e.Fecha });
        });

        // --- Client ---
        modelBuilder.Entity<Client>(entity =>
        {
            entity.ToTable("clients");
            entity.HasKey(e => e.IdClient);
            entity.Property(e => e.WhatsApp).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Nombre).HasMaxLength(150);
            entity.Property(e => e.Email).HasMaxLength(150);
            entity.Property(e => e.Notas).HasMaxLength(500);
            entity.Property(e => e.Activo).HasDefaultValue(true);
            entity.Property(e => e.FechaCreacion).HasDefaultValueSql("GETUTCDATE()");
            entity.HasIndex(e => new { e.IdTenant, e.WhatsApp }).IsUnique();
        });

        // --- Appointment ---
        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.ToTable("appointments");
            entity.HasKey(e => e.IdAppointment);
            entity.Property(e => e.Estado).IsRequired().HasMaxLength(20).HasDefaultValue("pending");
            entity.Property(e => e.ExternalEventId).HasMaxLength(500);
            entity.Property(e => e.MotivoCancelacion).HasMaxLength(500);
            entity.Property(e => e.Notas).HasMaxLength(1000);
            entity.Property(e => e.FechaCreacion).HasDefaultValueSql("GETUTCDATE()");
            entity.Property(e => e.FechaActualizacion).HasDefaultValueSql("GETUTCDATE()");

            entity.HasOne(e => e.Client)
                  .WithMany(c => c.Appointments)
                  .HasForeignKey(e => e.IdClient)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.ServiceType)
                  .WithMany()
                  .HasForeignKey(e => e.IdServiceType)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.IdTenant, e.FechaInicio });
            entity.HasIndex(e => e.ExternalEventId);
            entity.HasIndex(e => e.Estado);
        });
    }
}
