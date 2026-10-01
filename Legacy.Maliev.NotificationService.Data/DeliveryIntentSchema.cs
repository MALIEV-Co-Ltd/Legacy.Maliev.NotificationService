using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Application.Services;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.NotificationService.Data;

internal static class DeliveryIntentSchema
{
    internal static readonly IReadOnlyDictionary<string, string> Checks = new Dictionary<string, string>
    {
        ["CK_NotificationIntent_Identity"] = "\"ServiceSubject\" = 'service:legacy-accounting' AND length(\"Issuer\") > 0 AND \"Purpose\" = 'invoice-issued' AND \"ResourceType\" = 'invoice' AND \"ResourceId\" > 0 AND \"IntentId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"WorkflowOperationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"IntentId\" <> \"WorkflowOperationId\"",
        ["CK_NotificationIntent_Binding"] = "\"BindingVersion\" = 'notification-intent-hmac-v1' AND \"Binding\" ~ '^[0-9a-f]{64}$' AND \"KeyId\" ~ '^[A-Za-z0-9_-]{1,64}$'",
        ["CK_NotificationIntent_State"] = "\"State\" IN ('Admitted', 'Submitting', 'ProviderAccepted', 'OutcomeUnknown', 'RejectedBeforeSubmission') AND \"Channel\" IN ('Info', 'Manufacturing', 'NoReply', 'Support') AND \"Version\" > 0 AND \"UpdatedAt\" >= \"AdmittedAt\" AND ((\"State\" = 'ProviderAccepted' AND \"ProviderMessageId\" IS NOT NULL AND length(\"ProviderMessageId\") > 0) OR (\"State\" <> 'ProviderAccepted' AND \"ProviderMessageId\" IS NULL))",
    };

    private static readonly string[] Columns =
    [
        "AdmittedAt:timestamp with time zone:true:false", "Binding:character varying(64):true:false",
        "BindingVersion:character varying(64):true:false", "Channel:character varying(32):true:false",
        "IntentId:uuid:true:false", "Issuer:character varying(256):true:false", "KeyId:character varying(64):true:false",
        "ProviderMessageId:character varying(256):false:false", "Purpose:character varying(32):true:false",
        "ResourceId:integer:true:false", "ResourceType:character varying(32):true:false",
        "ServiceSubject:character varying(64):true:false", "State:character varying(32):true:false",
        "UpdatedAt:timestamp with time zone:true:false", "Version:bigint:true:false", "WorkflowOperationId:uuid:true:false",
    ];

    private static readonly IReadOnlyDictionary<string, string> CanonicalConstraints = new Dictionary<string, string>
    {
        ["AK_NotificationIntent_BusinessEffect"] = "UNIQUE (\"Issuer\", \"ServiceSubject\", \"Purpose\", \"ResourceType\", \"ResourceId\", \"WorkflowOperationId\")",
        ["PK_NotificationDeliveryIntent"] = "PRIMARY KEY (\"Issuer\", \"ServiceSubject\", \"IntentId\")",
        ["CK_NotificationIntent_Binding"] = "CHECK ((((\"BindingVersion\")::text = 'notification-intent-hmac-v1'::text) AND ((\"Binding\")::text ~ '^[0-9a-f]{64}$'::text) AND ((\"KeyId\")::text ~ '^[A-Za-z0-9_-]{1,64}$'::text)))",
        ["CK_NotificationIntent_Identity"] = "CHECK ((((\"ServiceSubject\")::text = 'service:legacy-accounting'::text) AND (length((\"Issuer\")::text) > 0) AND ((\"Purpose\")::text = 'invoice-issued'::text) AND ((\"ResourceType\")::text = 'invoice'::text) AND (\"ResourceId\" > 0) AND (\"IntentId\" <> '00000000-0000-0000-0000-000000000000'::uuid) AND (\"WorkflowOperationId\" <> '00000000-0000-0000-0000-000000000000'::uuid) AND (\"IntentId\" <> \"WorkflowOperationId\")))",
        ["CK_NotificationIntent_State"] = "CHECK ((((\"State\")::text = ANY ((ARRAY['Admitted'::character varying, 'Submitting'::character varying, 'ProviderAccepted'::character varying, 'OutcomeUnknown'::character varying, 'RejectedBeforeSubmission'::character varying])::text[])) AND ((\"Channel\")::text = ANY ((ARRAY['Info'::character varying, 'Manufacturing'::character varying, 'NoReply'::character varying, 'Support'::character varying])::text[])) AND (\"Version\" > 0) AND (\"UpdatedAt\" >= \"AdmittedAt\") AND ((((\"State\")::text = 'ProviderAccepted'::text) AND (\"ProviderMessageId\" IS NOT NULL) AND (length((\"ProviderMessageId\")::text) > 0)) OR (((\"State\")::text <> 'ProviderAccepted'::text) AND (\"ProviderMessageId\" IS NULL)))))",
    };

    internal static async Task EnsureReadyAsync(DeliveryIntentDbContext database, NotificationIntentBinding binding, CancellationToken token)
    {
        // Held until transaction disposal: prevents concurrent DDL replacing the inspected authority.
        await database.Database.ExecuteSqlRawAsync("LOCK TABLE public.\"NotificationDeliveryIntent\" IN ROW SHARE MODE", token);
        var table = await database.Database.SqlQueryRaw<string>("SELECT relkind::text || ':' || relpersistence::text || ':' || relrowsecurity::text AS \"Value\" FROM pg_class JOIN pg_namespace ON pg_namespace.oid=relnamespace WHERE nspname='public' AND relname='NotificationDeliveryIntent'").ToListAsync(token);
        if (!table.SequenceEqual(["r:p:false"])) throw new DeliveryIntentUnavailableException();
        var columns = await database.Database.SqlQueryRaw<string>("SELECT attname || ':' || format_type(atttypid,atttypmod) || ':' || attnotnull::text || ':' || atthasdef::text || ':' || attgenerated::text || ':' || attidentity::text AS \"Value\" FROM pg_attribute WHERE attrelid='public.\"NotificationDeliveryIntent\"'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attname COLLATE \"C\"").ToListAsync(token);
        if (!columns.SequenceEqual(Columns.Select(column => column + "::"))) throw new DeliveryIntentUnavailableException();
        var constraints = await database.Database.SqlQueryRaw<string>("SELECT conname || ':' || convalidated::text || ':' || condeferrable::text || ':' || condeferred::text || ':' || pg_get_constraintdef(oid) AS \"Value\" FROM pg_constraint WHERE conrelid='public.\"NotificationDeliveryIntent\"'::regclass AND contype IN ('p','u','c') ORDER BY conname COLLATE \"C\"").ToListAsync(token);
        var expected = CanonicalConstraints.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":true:false:false:" + pair.Value);
        if (!constraints.SequenceEqual(expected)) throw new DeliveryIntentUnavailableException();
        var extraConstraints = await database.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pg_constraint WHERE conrelid='public.\"NotificationDeliveryIntent\"'::regclass AND contype NOT IN ('p','u','c','n')").SingleAsync(token);
        var triggers = await database.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pg_trigger WHERE tgrelid='public.\"NotificationDeliveryIntent\"'::regclass AND NOT tgisinternal").SingleAsync(token);
        var rules = await database.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pg_rewrite WHERE ev_class='public.\"NotificationDeliveryIntent\"'::regclass").SingleAsync(token);
        if (extraConstraints != 0 || triggers != 0 || rules != 0) throw new DeliveryIntentUnavailableException();
        var keyIds = await database.Database.SqlQueryRaw<string>("SELECT DISTINCT \"KeyId\" AS \"Value\" FROM public.\"NotificationDeliveryIntent\"").ToListAsync(token);
        foreach (var keyId in keyIds) binding.EnsureAvailableKey(keyId);
    }
}
