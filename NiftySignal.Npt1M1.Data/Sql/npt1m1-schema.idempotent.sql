DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'npt1m1') THEN
        CREATE SCHEMA npt1m1;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS npt1m1.__ef_migrations_history (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___ef_migrations_history" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM npt1m1.__ef_migrations_history WHERE "MigrationId" = '20261012033928_InitialNpt1M1') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'npt1m1') THEN
            CREATE SCHEMA npt1m1;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM npt1m1.__ef_migrations_history WHERE "MigrationId" = '20261012033928_InitialNpt1M1') THEN
    CREATE TABLE npt1m1.frames (
        session_id character varying(64) NOT NULL,
        bar_seq integer NOT NULL,
        start_available_at_utc timestamp with time zone NOT NULL,
        end_available_at_utc timestamp with time zone NOT NULL,
        ready boolean NOT NULL,
        is_valid boolean NOT NULL,
        consecutive_valid_bars integer NOT NULL,
        microdev_time_weighted double precision,
        microdev_usable_s double precision,
        pe_band_contract_strict_delta bigint,
        band_center_strike double precision,
        band_expiry date,
        band_unavailable_reason character varying(256),
        frame_hash character varying(64) NOT NULL,
        canonical_text text NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_frames" PRIMARY KEY (session_id, bar_seq)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM npt1m1.__ef_migrations_history WHERE "MigrationId" = '20261012033928_InitialNpt1M1') THEN
    CREATE TABLE npt1m1.runtime (
        session_id character varying(64) NOT NULL,
        status character varying(32) NOT NULL,
        last_tick_id bigint NOT NULL,
        last_bar_seq integer NOT NULL,
        last_bar_end_utc timestamp with time zone,
        last_heartbeat_utc timestamp with time zone NOT NULL,
        polls bigint NOT NULL,
        last_poll_ms double precision NOT NULL,
        max_poll_ms double precision NOT NULL,
        rows_read bigint NOT NULL,
        divergent_frames bigint NOT NULL,
        detail text,
        CONSTRAINT "PK_runtime" PRIMARY KEY (session_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM npt1m1.__ef_migrations_history WHERE "MigrationId" = '20261012033928_InitialNpt1M1') THEN
    CREATE TABLE npt1m1.sessions (
        session_id character varying(64) NOT NULL,
        trade_date date NOT NULL,
        model_version character varying(64) NOT NULL,
        freeze_fingerprint character varying(64) NOT NULL,
        base_bar_volume bigint NOT NULL,
        opening_volume bigint NOT NULL,
        bar_size_status character varying(64) NOT NULL,
        decision_json text NOT NULL,
        report_json text NOT NULL,
        future_token character varying(64) NOT NULL,
        weekly_expiry date NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_sessions" PRIMARY KEY (session_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM npt1m1.__ef_migrations_history WHERE "MigrationId" = '20261012033928_InitialNpt1M1') THEN
    CREATE TABLE npt1m1.volume_history (
        trade_date date NOT NULL,
        future_token character varying(64) NOT NULL,
        future_symbol character varying(64) NOT NULL,
        expiry date NOT NULL,
        lot_size integer NOT NULL,
        opening_volume bigint NOT NULL,
        window_volume bigint NOT NULL,
        close_volume bigint NOT NULL,
        full_day_volume bigint NOT NULL,
        opening_coverage_minutes integer NOT NULL,
        window_coverage_minutes integer NOT NULL,
        first_tick_ist character varying(16) NOT NULL,
        last_tick_ist character varying(16) NOT NULL,
        futures_ticks bigint NOT NULL,
        baseline_eligible boolean NOT NULL,
        computed_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_volume_history" PRIMARY KEY (trade_date)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM npt1m1.__ef_migrations_history WHERE "MigrationId" = '20261012033928_InitialNpt1M1') THEN
    CREATE INDEX ix_frames_end ON npt1m1.frames (end_available_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM npt1m1.__ef_migrations_history WHERE "MigrationId" = '20261012033928_InitialNpt1M1') THEN
    CREATE UNIQUE INDEX ux_sessions_day_model ON npt1m1.sessions (trade_date, model_version);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM npt1m1.__ef_migrations_history WHERE "MigrationId" = '20261012033928_InitialNpt1M1') THEN
    INSERT INTO npt1m1.__ef_migrations_history ("MigrationId", "ProductVersion")
    VALUES ('20261012033928_InitialNpt1M1', '10.0.11');
    END IF;
END $EF$;
COMMIT;

