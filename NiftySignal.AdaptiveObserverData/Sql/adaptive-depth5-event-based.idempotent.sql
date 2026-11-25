START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_options_depth5_bars ADD "AskNotionalEventSum" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_options_depth5_bars ADD "BidNotionalEventSum" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_options_depth5_bars ADD "FeedGapEvents" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_options_depth5_bars ADD "MicroDev5EventSum" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_options_depth5_bars ADD "NoDepthEvents" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_options_depth5_bars ADD "RepeatSnapshotsIgnored" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_options_depth5_bars ADD "Tob5EventSum" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_options_depth5_bars ADD "ValidDepthEvents" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_options_depth5_bars ADD "ValidMicroDev5Events" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_futures_depth5_bars ADD "AskNotionalEventSum" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_futures_depth5_bars ADD "BidNotionalEventSum" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_futures_depth5_bars ADD "FeedGapEvents" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_futures_depth5_bars ADD "MicroDev5EventSum" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_futures_depth5_bars ADD "NoDepthEvents" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_futures_depth5_bars ADD "RepeatSnapshotsIgnored" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_futures_depth5_bars ADD "Tob5EventSum" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_futures_depth5_bars ADD "ValidDepthEvents" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    ALTER TABLE adaptive_futures_depth5_bars ADD "ValidMicroDev5Events" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125074852_AddDepth5EventBasedColumns') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261125074852_AddDepth5EventBasedColumns', '10.0.11');
    END IF;
END $EF$;
COMMIT;

