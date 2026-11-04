DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'pev2') THEN
        CREATE SCHEMA pev2;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS pev2.__pev2_migrations_history (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___pev2_migrations_history" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'pev2') THEN
            CREATE SCHEMA pev2;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE TABLE pev2.pev2_dispositions (
        session_id bigint NOT NULL,
        bar_seq integer NOT NULL,
        entries_allowed boolean NOT NULL,
        block_reasons integer NOT NULL,
        processing_lag_ms integer NOT NULL,
        run_length integer NOT NULL,
        previous_frame_usable boolean NOT NULL,
        processed_without_journal boolean NOT NULL,
        journal_seq bigint GENERATED ALWAYS AS IDENTITY,
        CONSTRAINT "PK_pev2_dispositions" PRIMARY KEY (session_id, bar_seq)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE TABLE pev2.pev2_events (
        event_id character varying(256) COLLATE "C" NOT NULL,
        strategy_id character varying(64) COLLATE "C" NOT NULL,
        config_hash character varying(64) COLLATE "C" NOT NULL,
        kind character varying(32) COLLATE "C" NOT NULL,
        bar_seq integer NOT NULL,
        at_utc_ticks bigint NOT NULL,
        payload_json text NOT NULL,
        CONSTRAINT "PK_pev2_events" PRIMARY KEY (event_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE TABLE pev2.pev2_frames (
        session_id bigint NOT NULL,
        bar_seq integer NOT NULL,
        frame_hash character varying(64) COLLATE "C" NOT NULL,
        payload_json text NOT NULL,
        bar_end_utc_ticks bigint NOT NULL,
        last_source_tick_id bigint NOT NULL,
        verified boolean NOT NULL,
        journal_seq bigint GENERATED ALWAYS AS IDENTITY,
        CONSTRAINT "PK_pev2_frames" PRIMARY KEY (session_id, bar_seq),
        CONSTRAINT ck_pev2_frames_hash CHECK (frame_hash ~ '^[0-9a-f]{64}$')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE TABLE pev2.pev2_gaps (
        id bigint GENERATED ALWAYS AS IDENTITY,
        session_id bigint NOT NULL,
        from_bar_seq integer NOT NULL,
        to_bar_seq integer NOT NULL,
        cause integer NOT NULL,
        producer_signals integer NOT NULL,
        CONSTRAINT "PK_pev2_gaps" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE TABLE pev2.pev2_schema_versions (
        component character varying(64) COLLATE "C" NOT NULL,
        schema_version integer NOT NULL,
        journal_schema character varying(64) COLLATE "C" NOT NULL,
        CONSTRAINT "PK_pev2_schema_versions" PRIMARY KEY (component)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE TABLE pev2.pev2_strategy_configs (
        config_hash character varying(64) COLLATE "C" NOT NULL,
        strategy_id character varying(64) COLLATE "C" NOT NULL,
        version integer NOT NULL,
        canonical_json text NOT NULL,
        recorded_utc_ticks bigint NOT NULL,
        CONSTRAINT "PK_pev2_strategy_configs" PRIMARY KEY (config_hash)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    INSERT INTO pev2.pev2_schema_versions (component, journal_schema, schema_version)
    VALUES ('paper-engine-v2', 'pev2-journal/1', 1);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE UNIQUE INDEX "IX_pev2_dispositions_journal_seq" ON pev2.pev2_dispositions (journal_seq);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE INDEX "IX_pev2_events_at_utc_ticks" ON pev2.pev2_events (at_utc_ticks);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE INDEX "IX_pev2_events_strategy_id_at_utc_ticks" ON pev2.pev2_events (strategy_id, at_utc_ticks);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE UNIQUE INDEX "IX_pev2_frames_journal_seq" ON pev2.pev2_frames (journal_seq);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE INDEX "IX_pev2_frames_session_id_bar_end_utc_ticks" ON pev2.pev2_frames (session_id, bar_end_utc_ticks);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE UNIQUE INDEX "IX_pev2_gaps_session_id_from_bar_seq_to_bar_seq" ON pev2.pev2_gaps (session_id, from_bar_seq, to_bar_seq);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    CREATE INDEX "IX_pev2_strategy_configs_strategy_id_version" ON pev2.pev2_strategy_configs (strategy_id, version);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031084515_InitialPaperEngineV2Schema') THEN
    INSERT INTO pev2.__pev2_migrations_history ("MigrationId", "ProductVersion")
    VALUES ('20261031084515_InitialPaperEngineV2Schema', '10.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031091214_AddAlertOutbox') THEN
    CREATE TABLE pev2.pev2_alert_outbox (
        alert_id character varying(300) COLLATE "C" NOT NULL,
        alert_class character varying(32) COLLATE "C" NOT NULL,
        strategy_id character varying(64) COLLATE "C" NOT NULL,
        event_id character varying(256) COLLATE "C" NOT NULL,
        message_text text NOT NULL,
        status integer NOT NULL,
        attempts integer NOT NULL,
        created_utc_ticks bigint NOT NULL,
        next_attempt_utc_ticks bigint NOT NULL,
        sent_utc_ticks bigint,
        telegram_message_id bigint,
        last_error text,
        CONSTRAINT "PK_pev2_alert_outbox" PRIMARY KEY (alert_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031091214_AddAlertOutbox') THEN
    UPDATE pev2.pev2_schema_versions SET schema_version = 2
    WHERE component = 'paper-engine-v2';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031091214_AddAlertOutbox') THEN
    CREATE INDEX "IX_pev2_alert_outbox_status_next_attempt_utc_ticks" ON pev2.pev2_alert_outbox (status, next_attempt_utc_ticks);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261031091214_AddAlertOutbox') THEN
    INSERT INTO pev2.__pev2_migrations_history ("MigrationId", "ProductVersion")
    VALUES ('20261031091214_AddAlertOutbox', '10.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261104130137_AddObservability') THEN
    CREATE TABLE pev2.pev2_live_marks (
        strategy_id character varying(64) COLLATE "C" NOT NULL,
        position_id character varying(256) COLLATE "C" NOT NULL,
        trade_date character varying(10) COLLATE "C" NOT NULL,
        mark_seq bigint NOT NULL,
        status character varying(16) COLLATE "C" NOT NULL,
        payload_json text NOT NULL,
        published_utc_ticks bigint NOT NULL,
        CONSTRAINT "PK_pev2_live_marks" PRIMARY KEY (strategy_id, position_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261104130137_AddObservability') THEN
    CREATE TABLE pev2.pev2_opportunities (
        opportunity_id character varying(300) COLLATE "C" NOT NULL,
        strategy_id character varying(64) COLLATE "C" NOT NULL,
        version integer NOT NULL,
        config_hash character varying(64) COLLATE "C" NOT NULL,
        session_id bigint NOT NULL,
        trade_date character varying(10) COLLATE "C" NOT NULL,
        bar_seq integer NOT NULL,
        stage character varying(16) COLLATE "C" NOT NULL,
        resolved_bar_seq integer NOT NULL,
        decision_utc_ticks bigint NOT NULL,
        direction character varying(16) COLLATE "C" NOT NULL,
        category integer NOT NULL,
        category_name character varying(48) COLLATE "C" NOT NULL,
        reason character varying(64) COLLATE "C" NOT NULL,
        detail text NOT NULL,
        in_position boolean NOT NULL,
        order_eligible boolean NOT NULL,
        order_attempted boolean NOT NULL,
        order_result character varying(24) COLLATE "C" NOT NULL,
        intent_id text,
        token text,
        event_ref text,
        frame_ref character varying(64) COLLATE "C" NOT NULL,
        entries_allowed boolean NOT NULL,
        coverage character varying(128) COLLATE "C" NOT NULL,
        recorded_utc_ticks bigint NOT NULL,
        CONSTRAINT "PK_pev2_opportunities" PRIMARY KEY (opportunity_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261104130137_AddObservability') THEN
    CREATE INDEX "IX_pev2_opportunities_trade_date_category" ON pev2.pev2_opportunities (trade_date, category);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261104130137_AddObservability') THEN
    CREATE INDEX "IX_pev2_opportunities_trade_date_strategy_id_bar_seq" ON pev2.pev2_opportunities (trade_date, strategy_id, bar_seq);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pev2.__pev2_migrations_history WHERE "MigrationId" = '20261104130137_AddObservability') THEN
    INSERT INTO pev2.__pev2_migrations_history ("MigrationId", "ProductVersion")
    VALUES ('20261104130137_AddObservability', '10.0.11');
    END IF;
END $EF$;
COMMIT;

