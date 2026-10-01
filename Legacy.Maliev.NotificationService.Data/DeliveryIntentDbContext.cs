using Legacy.Maliev.NotificationService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.NotificationService.Data;

/// <summary>Isolated additive notification authority; never migrates itself at startup.</summary>
public class DeliveryIntentDbContext(DbContextOptions<DeliveryIntentDbContext> options) : DbContext(options)
{
    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<DeliveryIntentRow>();
        row.ToTable("NotificationDeliveryIntent", "public", table =>
        {
            foreach (var constraint in DeliveryIntentSchema.Checks)
                table.HasCheckConstraint(constraint.Key, constraint.Value);
        });
        row.HasKey(value => new { value.Issuer, value.ServiceSubject, value.IntentId });
        row.HasAlternateKey(value => new { value.Issuer, value.ServiceSubject, value.Purpose, value.ResourceType, value.ResourceId, value.WorkflowOperationId })
            .HasName("AK_NotificationIntent_BusinessEffect");
        row.Property(value => value.Issuer).HasMaxLength(256);
        row.Property(value => value.ServiceSubject).HasMaxLength(64);
        row.Property(value => value.Purpose).HasMaxLength(32);
        row.Property(value => value.ResourceType).HasMaxLength(32);
        row.Property(value => value.Channel).HasMaxLength(32);
        row.Property(value => value.BindingVersion).HasMaxLength(64);
        row.Property(value => value.KeyId).HasMaxLength(64);
        row.Property(value => value.Binding).HasMaxLength(64);
        row.Property(value => value.State).HasMaxLength(32);
        row.Property(value => value.ProviderMessageId).HasMaxLength(256);
    }
}

internal sealed class DeliveryIntentRow
{
    public required string Issuer { get; set; }
    public required string ServiceSubject { get; set; }
    public Guid IntentId { get; set; }
    public required string Purpose { get; set; }
    public required string ResourceType { get; set; }
    public int ResourceId { get; set; }
    public Guid WorkflowOperationId { get; set; }
    public required string Channel { get; set; }
    public required string BindingVersion { get; set; }
    public required string KeyId { get; set; }
    public required string Binding { get; set; }
    public required string State { get; set; }
    public long Version { get; set; }
    public DateTime AdmittedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? ProviderMessageId { get; set; }

    public DeliveryIntentRecord Record() => new(new(Issuer, ServiceSubject, IntentId, Purpose, ResourceType,
        ResourceId, WorkflowOperationId, Enum.Parse<EmailChannel>(Channel)), BindingVersion, KeyId, Binding,
        Enum.Parse<DeliveryIntentState>(State), Version, AdmittedAt, UpdatedAt, ProviderMessageId);
}
