DO $$
DECLARE
    object record;
BEGIN
    FOR object IN
        SELECT n.nspname AS schema_name FROM pg_namespace n
        WHERE n.nspname IN ('identity', 'notes', 'jobs', 'outbox', 'tenancy')
    LOOP
        EXECUTE format('GRANT USAGE ON SCHEMA %I TO sb_runtime', object.schema_name);
        EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO sb_runtime', object.schema_name);
    END LOOP;

    FOR object IN
        SELECT schemaname AS schema_name, tablename AS table_name FROM pg_tables
        WHERE schemaname IN ('identity', 'notes', 'jobs', 'outbox', 'tenancy')
    LOOP
        IF object.schema_name = 'tenancy' AND object.table_name = 'Documents' THEN
            GRANT USAGE ON SCHEMA tenancy TO sb_tenant;
            GRANT SELECT, INSERT, UPDATE, DELETE ON tenancy."Documents" TO sb_tenant;
        ELSIF object.table_name = '__EFMigrationsHistory' THEN
            EXECUTE format('GRANT SELECT ON %I.%I TO sb_runtime', object.schema_name, object.table_name);
        ELSIF object.schema_name = 'identity' AND object.table_name = 'AccessAudits' THEN
            GRANT SELECT, INSERT ON identity."AccessAudits" TO sb_runtime;
        ELSE
            EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON %I.%I TO sb_runtime',
                object.schema_name, object.table_name);
        END IF;
    END LOOP;
END $$;
