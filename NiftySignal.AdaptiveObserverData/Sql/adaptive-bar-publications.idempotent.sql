START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126053253_AddBarPublications') THEN
    CREATE TABLE adaptive_bar_publications (
        "SessionId" bigint NOT NULL,
        "BarSeq" integer NOT NULL,
        "Verified" boolean NOT NULL,
        "UnverifiedReason" character varying(128),
        "PublishedAtUtc" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_adaptive_bar_publications" PRIMARY KEY ("SessionId", "BarSeq"),
        CONSTRAINT "FK_adaptive_bar_publications_adaptive_sessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES adaptive_sessions ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126053253_AddBarPublications') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261126053253_AddBarPublications', '10.0.11');
    END IF;
END $EF$;
COMMIT;

