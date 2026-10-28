START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261028130033_AddSpotAtmRuleAndStrikeGreeks') THEN
    ALTER TABLE adaptive_sessions ADD "AtmRuleVersion" character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261028130033_AddSpotAtmRuleAndStrikeGreeks') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "DeltaChange" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261028130033_AddSpotAtmRuleAndStrikeGreeks') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "DeltaEnd" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261028130033_AddSpotAtmRuleAndStrikeGreeks') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "DeltaStart" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261028130033_AddSpotAtmRuleAndStrikeGreeks') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "VegaChange" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261028130033_AddSpotAtmRuleAndStrikeGreeks') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "VegaEnd" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261028130033_AddSpotAtmRuleAndStrikeGreeks') THEN
    ALTER TABLE adaptive_option_strike_bars ADD "VegaStart" double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261028130033_AddSpotAtmRuleAndStrikeGreeks') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261028130033_AddSpotAtmRuleAndStrikeGreeks', '10.0.11');
    END IF;
END $EF$;
COMMIT;

