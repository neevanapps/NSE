START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "GrossOfi" bigint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "MicroDevTimeWeighted" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "NetOfi" bigint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "OfiTransitions" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "TobTimeWeighted" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "TobUsableSeconds" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi') THEN
    ALTER TABLE adaptive_futures_supplemental_bars ADD "GrossOfi" bigint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi', '10.0.11');
    END IF;
END $EF$;
COMMIT;

