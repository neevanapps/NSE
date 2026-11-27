START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126043514_AddObserverRecoveryProgress') THEN
    ALTER TABLE adaptive_observer_runtime ADD "LastRecoveryProgressUtc" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126043514_AddObserverRecoveryProgress') THEN
    ALTER TABLE adaptive_observer_runtime ADD "RecoveryBarsReplayed" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126043514_AddObserverRecoveryProgress') THEN
    ALTER TABLE adaptive_observer_runtime ADD "RecoveryFailureCount" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126043514_AddObserverRecoveryProgress') THEN
    ALTER TABLE adaptive_observer_runtime ADD "RecoveryPersistedBarsAtStart" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126043514_AddObserverRecoveryProgress') THEN
    ALTER TABLE adaptive_observer_runtime ADD "RecoveryPhase" character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126043514_AddObserverRecoveryProgress') THEN
    ALTER TABLE adaptive_observer_runtime ADD "RecoveryReplayedThroughUtc" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126043514_AddObserverRecoveryProgress') THEN
    ALTER TABLE adaptive_observer_runtime ADD "RecoveryTicksProcessed" bigint NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126043514_AddObserverRecoveryProgress') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261126043514_AddObserverRecoveryProgress', '10.0.11');
    END IF;
END $EF$;
COMMIT;

