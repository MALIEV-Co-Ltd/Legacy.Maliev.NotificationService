using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Legacy.Maliev.NotificationService.Data.Migrations;

/// <summary>Adds retained notification authority without touching legacy data.</summary>
[DbContext(typeof(DeliveryIntentDbContext))]
[Migration("20261001143000_AddNotificationDeliveryIntent")]
public sealed class AddNotificationDeliveryIntent : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("NotificationDeliveryIntent", schema: "public", columns: table => new
        {
            Issuer = table.Column<string>(type: "character varying(256)", nullable: false),
            ServiceSubject = table.Column<string>(type: "character varying(64)", nullable: false),
            IntentId = table.Column<Guid>(type: "uuid", nullable: false),
            Purpose = table.Column<string>(type: "character varying(32)", nullable: false),
            ResourceType = table.Column<string>(type: "character varying(32)", nullable: false),
            ResourceId = table.Column<int>(type: "integer", nullable: false),
            WorkflowOperationId = table.Column<Guid>(type: "uuid", nullable: false),
            Channel = table.Column<string>(type: "character varying(32)", nullable: false),
            BindingVersion = table.Column<string>(type: "character varying(64)", nullable: false),
            KeyId = table.Column<string>(type: "character varying(64)", nullable: false),
            Binding = table.Column<string>(type: "character varying(64)", nullable: false),
            State = table.Column<string>(type: "character varying(32)", nullable: false),
            Version = table.Column<long>(type: "bigint", nullable: false),
            AdmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            ProviderMessageId = table.Column<string>(type: "character varying(256)", nullable: true),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_NotificationDeliveryIntent", row => new { row.Issuer, row.ServiceSubject, row.IntentId });
            table.UniqueConstraint("AK_NotificationIntent_BusinessEffect", row => new { row.Issuer, row.ServiceSubject, row.Purpose, row.ResourceType, row.ResourceId, row.WorkflowOperationId });
            table.CheckConstraint("CK_NotificationIntent_Identity", "\"ServiceSubject\" = 'service:legacy-accounting' AND length(\"Issuer\") > 0 AND \"Purpose\" = 'invoice-issued' AND \"ResourceType\" = 'invoice' AND \"ResourceId\" > 0 AND \"IntentId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"WorkflowOperationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"IntentId\" <> \"WorkflowOperationId\"");
            table.CheckConstraint("CK_NotificationIntent_Binding", "\"BindingVersion\" = 'notification-intent-hmac-v1' AND \"Binding\" ~ '^[0-9a-f]{64}$' AND \"KeyId\" ~ '^[A-Za-z0-9_-]{1,64}$'");
            table.CheckConstraint("CK_NotificationIntent_State", "\"State\" IN ('Admitted', 'Submitting', 'ProviderAccepted', 'OutcomeUnknown', 'RejectedBeforeSubmission') AND \"Channel\" IN ('Info', 'Manufacturing', 'NoReply', 'Support') AND \"Version\" > 0 AND \"UpdatedAt\" >= \"AdmittedAt\" AND ((\"State\" = 'ProviderAccepted' AND \"ProviderMessageId\" IS NOT NULL AND length(\"ProviderMessageId\") > 0) OR (\"State\" <> 'ProviderAccepted' AND \"ProviderMessageId\" IS NULL))");
        });
    }

    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.12").HasAnnotation("Relational:MaxIdentifierLength", 63);
        modelBuilder.UseIdentityByDefaultColumns();
        modelBuilder.Entity("Legacy.Maliev.NotificationService.Data.DeliveryIntentRow", row =>
        {
            row.Property<string>("Issuer").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            row.Property<string>("ServiceSubject").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            row.Property<string>("Purpose").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            row.Property<string>("ResourceType").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            row.Property<string>("Channel").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            row.Property<string>("BindingVersion").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            row.Property<string>("KeyId").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            row.Property<string>("Binding").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            row.Property<string>("State").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            row.Property<string>("ProviderMessageId").HasMaxLength(256).HasColumnType("character varying(256)");
            row.Property<Guid>("IntentId").HasColumnType("uuid");
            row.Property<Guid>("WorkflowOperationId").HasColumnType("uuid");
            row.Property<int>("ResourceId").HasColumnType("integer");
            row.Property<long>("Version").HasColumnType("bigint");
            row.Property<DateTime>("AdmittedAt").HasColumnType("timestamp with time zone");
            row.Property<DateTime>("UpdatedAt").HasColumnType("timestamp with time zone");
            row.HasKey("Issuer", "ServiceSubject", "IntentId");
            row.HasAlternateKey("Issuer", "ServiceSubject", "Purpose", "ResourceType", "ResourceId", "WorkflowOperationId").HasName("AK_NotificationIntent_BusinessEffect");
            row.ToTable("NotificationDeliveryIntent", "public", table =>
            {
                table.HasCheckConstraint("CK_NotificationIntent_Identity", "\"ServiceSubject\" = 'service:legacy-accounting' AND length(\"Issuer\") > 0 AND \"Purpose\" = 'invoice-issued' AND \"ResourceType\" = 'invoice' AND \"ResourceId\" > 0 AND \"IntentId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"WorkflowOperationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"IntentId\" <> \"WorkflowOperationId\"");
                table.HasCheckConstraint("CK_NotificationIntent_Binding", "\"BindingVersion\" = 'notification-intent-hmac-v1' AND \"Binding\" ~ '^[0-9a-f]{64}$' AND \"KeyId\" ~ '^[A-Za-z0-9_-]{1,64}$'");
                table.HasCheckConstraint("CK_NotificationIntent_State", "\"State\" IN ('Admitted', 'Submitting', 'ProviderAccepted', 'OutcomeUnknown', 'RejectedBeforeSubmission') AND \"Channel\" IN ('Info', 'Manufacturing', 'NoReply', 'Support') AND \"Version\" > 0 AND \"UpdatedAt\" >= \"AdmittedAt\" AND ((\"State\" = 'ProviderAccepted' AND \"ProviderMessageId\" IS NOT NULL AND length(\"ProviderMessageId\") > 0) OR (\"State\" <> 'ProviderAccepted' AND \"ProviderMessageId\" IS NULL))");
            });
        });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Notification authority must be retained; destructive rollback is forbidden.");
}
