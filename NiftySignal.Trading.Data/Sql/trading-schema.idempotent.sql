CREATE TABLE IF NOT EXISTS __ef_migrations_history (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___ef_migrations_history" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE run_owner (
        run_id character varying(256) COLLATE "C" NOT NULL,
        epoch bigint NOT NULL,
        owner_id character varying(200) COLLATE "C" NOT NULL,
        acquired_at_utc timestamp with time zone,
        lease_expires_at_utc timestamp with time zone,
        CONSTRAINT pk_run_owner PRIMARY KEY (run_id),
        CONSTRAINT ck_run_owner_epoch CHECK (epoch >= 1)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE runs (
        run_id character varying(256) COLLATE "C" NOT NULL,
        trade_date date NOT NULL,
        schema_version integer NOT NULL,
        codec_version character varying(64) COLLATE "C" NOT NULL,
        state_digest_version character varying(64) COLLATE "C" NOT NULL,
        engine_compatibility_id character varying(128) COLLATE "C" NOT NULL,
        config_schema_version integer NOT NULL,
        config_hash character varying(128) COLLATE "C" NOT NULL,
        freeze_hash character varying(128) COLLATE "C" NOT NULL,
        translator_options_hash character varying(128) COLLATE "C" NOT NULL,
        readiness_spec text COLLATE "C" NOT NULL,
        retention_hash character varying(128) COLLATE "C" NOT NULL,
        strategies_identity text COLLATE "C" NOT NULL,
        durable_options_hash character varying(128) COLLATE "C" NOT NULL,
        source_manifest_hash character varying(128) COLLATE "C" NOT NULL,
        provenance_git_commit character varying(64) COLLATE "C",
        provenance_tree_clean boolean,
        provenance_sdk character varying(64) COLLATE "C" NOT NULL,
        provenance_assembly_hashes text COLLATE "C" NOT NULL,
        config_json text COLLATE "C" NOT NULL,
        freeze_snapshot text COLLATE "C" NOT NULL,
        source_manifest text COLLATE "C" NOT NULL,
        horizon_utc_ticks bigint NOT NULL,
        content_hash character(64) COLLATE "C" NOT NULL,
        completed boolean NOT NULL,
        CONSTRAINT pk_runs PRIMARY KEY (run_id),
        CONSTRAINT ck_runs_content_hash CHECK (content_hash ~ '^[0-9A-F]{64}$')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE schema_versions (
        component character varying(64) COLLATE "C" NOT NULL,
        schema_version integer NOT NULL,
        minimum_compatible_code character varying(200) COLLATE "C" NOT NULL,
        codec_version character varying(64) COLLATE "C" NOT NULL,
        state_digest_version character varying(64) COLLATE "C" NOT NULL,
        CONSTRAINT pk_schema_versions PRIMARY KEY (component)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE accounts (
        run_id character varying(256) COLLATE "C" NOT NULL,
        account_id character varying(256) COLLATE "C" NOT NULL,
        CONSTRAINT pk_accounts PRIMARY KEY (run_id, account_id),
        CONSTRAINT fk_accounts_runs_run_id FOREIGN KEY (run_id) REFERENCES runs (run_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE control_journal (
        run_id character varying(256) COLLATE "C" NOT NULL,
        control_id character varying(256) COLLATE "C" NOT NULL,
        journal_seq bigint NOT NULL,
        kind character varying(32) COLLATE "C" NOT NULL,
        control_key bigint NOT NULL,
        available_at_utc_ticks bigint NOT NULL,
        available_at_utc timestamp with time zone NOT NULL,
        after_offered_rows bigint NOT NULL,
        payload text COLLATE "C" NOT NULL,
        content_hash character(64) COLLATE "C" NOT NULL,
        codec_version character varying(64) COLLATE "C" NOT NULL,
        CONSTRAINT pk_control_journal PRIMARY KEY (run_id, control_id),
        CONSTRAINT ck_control_journal_hash CHECK (content_hash ~ '^[0-9A-F]{64}$'),
        CONSTRAINT ck_control_journal_kind CHECK (kind IN ('Operator','Health','SourceProgress','Advance')),
        CONSTRAINT ck_control_journal_seq CHECK (journal_seq >= 1),
        CONSTRAINT fk_control_journal_runs_run_id FOREIGN KEY (run_id) REFERENCES runs (run_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE final_records (
        run_id character varying(256) COLLATE "C" NOT NULL,
        content_hash character(64) COLLATE "C" NOT NULL,
        codec_version character varying(64) COLLATE "C" NOT NULL,
        CONSTRAINT pk_final_records PRIMARY KEY (run_id),
        CONSTRAINT ck_final_records_hash CHECK (content_hash ~ '^[0-9A-F]{64}$'),
        CONSTRAINT fk_final_records_runs_run_id FOREIGN KEY (run_id) REFERENCES runs (run_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE recovery_log (
        run_id character varying(256) COLLATE "C" NOT NULL,
        log_seq bigint NOT NULL,
        attempt integer NOT NULL,
        owner_epoch bigint NOT NULL,
        durable_steps bigint NOT NULL,
        durable_outputs bigint NOT NULL,
        durable_controls bigint NOT NULL,
        outcome character varying(128) COLLATE "C" NOT NULL,
        detail text COLLATE "C" NOT NULL,
        recorded_at_utc timestamp with time zone,
        CONSTRAINT pk_recovery_log PRIMARY KEY (run_id, log_seq),
        CONSTRAINT ck_recovery_log_seq CHECK (log_seq >= 1),
        CONSTRAINT fk_recovery_log_runs_run_id FOREIGN KEY (run_id) REFERENCES runs (run_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE steps (
        run_id character varying(256) COLLATE "C" NOT NULL,
        step_seq bigint NOT NULL,
        kind character varying(32) COLLATE "C" NOT NULL,
        units integer NOT NULL,
        engine_time_utc_ticks bigint NOT NULL,
        offered_rows bigint NOT NULL,
        first_group_ordinal bigint,
        last_group_ordinal bigint,
        last_group_at_utc_ticks bigint,
        last_source_id bigint,
        output_from bigint NOT NULL,
        output_to_exclusive bigint NOT NULL,
        content_hash character(64) COLLATE "C" NOT NULL,
        codec_version character varying(64) COLLATE "C" NOT NULL,
        CONSTRAINT pk_steps PRIMARY KEY (run_id, step_seq),
        CONSTRAINT ck_steps_hash CHECK (content_hash ~ '^[0-9A-F]{64}$'),
        CONSTRAINT ck_steps_kind CHECK (kind IN ('DataGroup','ControlOnly','Advance','Finish')),
        CONSTRAINT ck_steps_output_range CHECK (output_from >= 0 AND output_to_exclusive >= output_from),
        CONSTRAINT ck_steps_seq CHECK (step_seq >= 0),
        CONSTRAINT ck_steps_units CHECK (units >= 1),
        CONSTRAINT fk_steps_runs_run_id FOREIGN KEY (run_id) REFERENCES runs (run_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE variants (
        run_id character varying(256) COLLATE "C" NOT NULL,
        variant_id character varying(256) COLLATE "C" NOT NULL,
        variant_hash character varying(128) COLLATE "C" NOT NULL,
        strategy_id character varying(200) COLLATE "C" NOT NULL,
        strategy_version character varying(200) COLLATE "C" NOT NULL,
        account_id character varying(256) COLLATE "C" NOT NULL,
        CONSTRAINT pk_variants PRIMARY KEY (run_id, variant_id),
        CONSTRAINT fk_variants_accounts_run_id_account_id FOREIGN KEY (run_id, account_id) REFERENCES accounts (run_id, account_id) ON DELETE RESTRICT,
        CONSTRAINT fk_variants_runs_run_id FOREIGN KEY (run_id) REFERENCES runs (run_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE final_lines (
        run_id character varying(256) COLLATE "C" NOT NULL,
        channel character varying(16) COLLATE "C" NOT NULL,
        line_no integer NOT NULL,
        text text COLLATE "C" NOT NULL,
        CONSTRAINT pk_final_lines PRIMARY KEY (run_id, channel, line_no),
        CONSTRAINT ck_final_lines_channel CHECK (channel IN ('audit','summaries')),
        CONSTRAINT ck_final_lines_line_no CHECK (line_no >= 0),
        CONSTRAINT fk_final_lines_final_records_run_id FOREIGN KEY (run_id) REFERENCES final_records (run_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE checkpoints (
        run_id character varying(256) COLLATE "C" NOT NULL,
        step_seq bigint NOT NULL,
        engine_time_utc_ticks bigint NOT NULL,
        state_digest character(64) COLLATE "C" NOT NULL,
        state_digest_version character varying(64) COLLATE "C" NOT NULL,
        state_text text COLLATE "C",
        CONSTRAINT pk_checkpoints PRIMARY KEY (run_id, step_seq),
        CONSTRAINT ck_checkpoints_digest CHECK (state_digest ~ '^[0-9A-F]{64}$'),
        CONSTRAINT fk_checkpoints_steps_run_id_step_seq FOREIGN KEY (run_id, step_seq) REFERENCES steps (run_id, step_seq) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE control_applications (
        run_id character varying(256) COLLATE "C" NOT NULL,
        control_id character varying(256) COLLATE "C" NOT NULL,
        step_seq bigint NOT NULL,
        ordinal integer NOT NULL,
        CONSTRAINT pk_control_applications PRIMARY KEY (run_id, control_id),
        CONSTRAINT ck_control_applications_ordinal CHECK (ordinal >= 0),
        CONSTRAINT fk_control_applications_control_journal_run_id_control_id FOREIGN KEY (run_id, control_id) REFERENCES control_journal (run_id, control_id) ON DELETE RESTRICT,
        CONSTRAINT fk_control_applications_steps_run_id_step_seq FOREIGN KEY (run_id, step_seq) REFERENCES steps (run_id, step_seq) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE engine_outputs (
        run_id character varying(256) COLLATE "C" NOT NULL,
        seq bigint NOT NULL,
        step_seq bigint NOT NULL,
        type character varying(128) COLLATE "C" NOT NULL,
        variant_id character varying(200) COLLATE "C" NOT NULL,
        at_utc_ticks bigint NOT NULL,
        at_utc timestamp with time zone NOT NULL,
        payload text COLLATE "C" NOT NULL,
        content_hash character(64) COLLATE "C" NOT NULL,
        codec_version character varying(64) COLLATE "C" NOT NULL,
        CONSTRAINT pk_engine_outputs PRIMARY KEY (run_id, seq),
        CONSTRAINT ck_engine_outputs_hash CHECK (content_hash ~ '^[0-9A-F]{64}$'),
        CONSTRAINT ck_engine_outputs_seq CHECK (seq >= 0),
        CONSTRAINT fk_engine_outputs_steps_run_id_step_seq FOREIGN KEY (run_id, step_seq) REFERENCES steps (run_id, step_seq) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE step_digests (
        run_id character varying(256) COLLATE "C" NOT NULL,
        step_seq bigint NOT NULL,
        channel character varying(32) COLLATE "C" NOT NULL,
        line_count bigint NOT NULL,
        hash character(64) COLLATE "C" NOT NULL,
        CONSTRAINT pk_step_digests PRIMARY KEY (run_id, step_seq, channel),
        CONSTRAINT ck_step_digests_channel CHECK (channel IN ('audit','bars','candles','opportunities','outputs','quotes','readiness','rowQuarantine','summaries')),
        CONSTRAINT ck_step_digests_count CHECK (line_count >= 0),
        CONSTRAINT ck_step_digests_hash CHECK (hash ~ '^[0-9A-F]{64}$'),
        CONSTRAINT fk_step_digests_steps_run_id_step_seq FOREIGN KEY (run_id, step_seq) REFERENCES steps (run_id, step_seq) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE audit_entries (
        run_id character varying(256) COLLATE "C" NOT NULL,
        output_seq bigint NOT NULL,
        variant_id character varying(256) COLLATE "C" NOT NULL,
        opportunity_id character varying(256) COLLATE "C",
        stage character varying(200) COLLATE "C" NOT NULL,
        outcome character varying(200) COLLATE "C" NOT NULL,
        detail text COLLATE "C" NOT NULL,
        CONSTRAINT pk_audit_entries PRIMARY KEY (run_id, output_seq),
        CONSTRAINT fk_audit_entries_engine_outputs_run_id_output_seq FOREIGN KEY (run_id, output_seq) REFERENCES engine_outputs (run_id, seq) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE positions (
        run_id character varying(256) COLLATE "C" NOT NULL,
        position_id character varying(256) COLLATE "C" NOT NULL,
        variant_id character varying(256) COLLATE "C" NOT NULL,
        account_id character varying(256) COLLATE "C" NOT NULL,
        slot_no integer NOT NULL,
        opportunity_id character varying(256) COLLATE "C" NOT NULL,
        token character varying(64) COLLATE "C" NOT NULL,
        opened_output_seq bigint NOT NULL,
        closed_output_seq bigint,
        CONSTRAINT pk_positions PRIMARY KEY (run_id, position_id),
        CONSTRAINT ck_positions_closed_after_opened CHECK (closed_output_seq IS NULL OR closed_output_seq > opened_output_seq),
        CONSTRAINT ck_positions_slot CHECK (slot_no >= 1),
        CONSTRAINT fk_positions_accounts_run_id_account_id FOREIGN KEY (run_id, account_id) REFERENCES accounts (run_id, account_id) ON DELETE RESTRICT,
        CONSTRAINT fk_positions_engine_outputs_run_id_closed_output_seq FOREIGN KEY (run_id, closed_output_seq) REFERENCES engine_outputs (run_id, seq) ON DELETE RESTRICT,
        CONSTRAINT fk_positions_engine_outputs_run_id_opened_output_seq FOREIGN KEY (run_id, opened_output_seq) REFERENCES engine_outputs (run_id, seq) ON DELETE RESTRICT,
        CONSTRAINT fk_positions_variants_run_id_variant_id FOREIGN KEY (run_id, variant_id) REFERENCES variants (run_id, variant_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE orders (
        run_id character varying(256) COLLATE "C" NOT NULL,
        order_id character varying(256) COLLATE "C" NOT NULL,
        variant_id character varying(256) COLLATE "C" NOT NULL,
        kind character varying(16) COLLATE "C" NOT NULL,
        opportunity_id character varying(256) COLLATE "C",
        slot_no integer,
        position_id character varying(256) COLLATE "C",
        created_output_seq bigint NOT NULL,
        CONSTRAINT pk_orders PRIMARY KEY (run_id, order_id),
        CONSTRAINT ck_orders_entry_has_slot CHECK (kind <> 'Entry' OR slot_no >= 1),
        CONSTRAINT ck_orders_exit_has_position CHECK (kind <> 'Exit' OR position_id IS NOT NULL),
        CONSTRAINT ck_orders_kind CHECK (kind IN ('Entry','Exit')),
        CONSTRAINT fk_orders_engine_outputs_run_id_created_output_seq FOREIGN KEY (run_id, created_output_seq) REFERENCES engine_outputs (run_id, seq) ON DELETE RESTRICT,
        CONSTRAINT fk_orders_positions_run_id_position_id FOREIGN KEY (run_id, position_id) REFERENCES positions (run_id, position_id) ON DELETE RESTRICT,
        CONSTRAINT fk_orders_variants_run_id_variant_id FOREIGN KEY (run_id, variant_id) REFERENCES variants (run_id, variant_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE fills (
        run_id character varying(256) COLLATE "C" NOT NULL,
        fill_id character varying(256) COLLATE "C" NOT NULL,
        order_id character varying(256) COLLATE "C" NOT NULL,
        position_id character varying(256) COLLATE "C" NOT NULL,
        variant_id character varying(256) COLLATE "C" NOT NULL,
        account_id character varying(256) COLLATE "C" NOT NULL,
        output_seq bigint NOT NULL,
        side character varying(8) COLLATE "C" NOT NULL,
        quantity bigint NOT NULL,
        price_text character varying(64) COLLATE "C" NOT NULL,
        notional_text character varying(64) COLLATE "C" NOT NULL,
        CONSTRAINT pk_fills PRIMARY KEY (run_id, fill_id),
        CONSTRAINT ck_fills_quantity CHECK (quantity > 0),
        CONSTRAINT ck_fills_side CHECK (side IN ('Buy','Sell')),
        CONSTRAINT fk_fills_accounts_run_id_account_id FOREIGN KEY (run_id, account_id) REFERENCES accounts (run_id, account_id) ON DELETE RESTRICT,
        CONSTRAINT fk_fills_engine_outputs_run_id_output_seq FOREIGN KEY (run_id, output_seq) REFERENCES engine_outputs (run_id, seq) ON DELETE RESTRICT,
        CONSTRAINT fk_fills_orders_run_id_order_id FOREIGN KEY (run_id, order_id) REFERENCES orders (run_id, order_id) ON DELETE RESTRICT,
        CONSTRAINT fk_fills_positions_run_id_position_id FOREIGN KEY (run_id, position_id) REFERENCES positions (run_id, position_id) ON DELETE RESTRICT,
        CONSTRAINT fk_fills_variants_run_id_variant_id FOREIGN KEY (run_id, variant_id) REFERENCES variants (run_id, variant_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE TABLE ledger_entries (
        run_id character varying(256) COLLATE "C" NOT NULL,
        fill_id character varying(256) COLLATE "C" NOT NULL,
        component character varying(32) COLLATE "C" NOT NULL,
        amount_text character varying(64) COLLATE "C" NOT NULL,
        CONSTRAINT pk_ledger_entries PRIMARY KEY (run_id, fill_id, component),
        CONSTRAINT ck_ledger_entries_component CHECK (component IN ('Brokerage','Stt','ExchangeCharge','SebiFee','StampDuty','Gst','Notional')),
        CONSTRAINT fk_ledger_entries_fills_run_id_fill_id FOREIGN KEY (run_id, fill_id) REFERENCES fills (run_id, fill_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    INSERT INTO schema_versions (component, codec_version, minimum_compatible_code, schema_version, state_digest_version)
    VALUES ('trading-engine', 'trading-codec/1', 'trading-engine/cp6.1', 1, 'trading-state/1');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE UNIQUE INDEX ux_control_applications_run_id_step_seq_ordinal ON control_applications (run_id, step_seq, ordinal);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE UNIQUE INDEX ux_control_journal_run_id_journal_seq ON control_journal (run_id, journal_seq);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_engine_outputs_run_id_step_seq ON engine_outputs (run_id, step_seq);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_engine_outputs_run_id_variant_id_at_utc_ticks ON engine_outputs (run_id, variant_id, at_utc_ticks);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_fills_run_id_account_id ON fills (run_id, account_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_fills_run_id_order_id ON fills (run_id, order_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_fills_run_id_output_seq ON fills (run_id, output_seq);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_fills_run_id_position_id ON fills (run_id, position_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_fills_run_id_variant_id ON fills (run_id, variant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_orders_run_id_created_output_seq ON orders (run_id, created_output_seq);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_orders_run_id_position_id ON orders (run_id, position_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_orders_run_id_variant_id ON orders (run_id, variant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_positions_run_id_account_id ON positions (run_id, account_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_positions_run_id_closed_output_seq ON positions (run_id, closed_output_seq);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_positions_run_id_opened_output_seq ON positions (run_id, opened_output_seq);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE UNIQUE INDEX ux_positions_open_slot ON positions (run_id, variant_id, slot_no) WHERE closed_output_seq IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_runs_trade_date ON runs (trade_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    CREATE INDEX ix_variants_run_id_account_id ON variants (run_id, account_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "MigrationId" = '20261009145717_InitialTradingSchema') THEN
    INSERT INTO __ef_migrations_history ("MigrationId", "ProductVersion")
    VALUES ('20261009145717_InitialTradingSchema', '10.0.11');
    END IF;
END $EF$;
COMMIT;

