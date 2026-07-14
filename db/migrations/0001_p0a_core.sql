-- MyScent P0-A core persistence schema
-- Target: MySQL 8.x
-- All timestamps are UTC and use microsecond precision.
-- Current-state tables are projections; myscent_formula_event is append-only audit history.

SET NAMES utf8mb4;
SET time_zone = '+00:00';

CREATE TABLE IF NOT EXISTS myscent_schema_migration (
    migration_id        varchar(64)  NOT NULL,
    checksum_sha256     char(64)     NOT NULL,
    applied_at          datetime(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (migration_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS myscent_formula (
    formula_id                  varchar(36)  NOT NULL,
    actor_scope                 varchar(128) NOT NULL,
    owner_id                    varchar(64)  NULL,
    guest_id                    varchar(64)  NULL,
    title                       varchar(128) NULL,
    status                      varchar(32)  NOT NULL DEFAULT 'editing',
    revision                    bigint       NOT NULL DEFAULT 0,
    formula_hash                char(64)     NOT NULL,
    aggregate_state_hash        char(64)     NOT NULL,
    current_scene_id            varchar(64)  NOT NULL,
    engine_version              varchar(32)  NOT NULL,
    block_library_version       varchar(32)  NOT NULL,
    relation_library_version    varchar(32)  NOT NULL,
    current_analysis_id         varchar(36)  NULL,
    current_analysis_revision   bigint       NULL,
    source_formula_version_id   varchar(36)  NULL,
    created_at                  datetime(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at                  datetime(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (formula_id),
    KEY ix_formula_actor_updated (actor_scope, updated_at),
    KEY ix_formula_status_updated (status, updated_at),
    CONSTRAINT ck_formula_revision_nonnegative CHECK (revision >= 0),
    CONSTRAINT ck_formula_status CHECK (status IN ('editing', 'sealed')),
    CONSTRAINT ck_formula_identity CHECK (
        (owner_id IS NOT NULL AND guest_id IS NULL)
        OR (owner_id IS NULL AND guest_id IS NOT NULL)
    )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS myscent_formula_component_current (
    formula_id              varchar(36)   NOT NULL,
    component_key           varchar(128)  NOT NULL,
    block_id                varchar(64)   NULL,
    external_material_id    varchar(64)   NULL,
    relative_quantity       decimal(18,6) NOT NULL,
    stage_role_override     varchar(32)   NULL,
    added_order             int           NOT NULL,
    source_type             varchar(32)   NOT NULL DEFAULT 'builtin',
    created_at              datetime(6)   NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at              datetime(6)   NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (formula_id, component_key),
    KEY ix_component_block (block_id),
    CONSTRAINT fk_component_formula
        FOREIGN KEY (formula_id) REFERENCES myscent_formula (formula_id)
        ON DELETE CASCADE,
    CONSTRAINT ck_component_quantity_positive CHECK (relative_quantity >= 0.000001),
    CONSTRAINT ck_component_source_type CHECK (source_type IN ('builtin', 'external')),
    CONSTRAINT ck_component_identity CHECK (
        (source_type = 'builtin' AND block_id IS NOT NULL AND external_material_id IS NULL)
        OR (source_type = 'external' AND external_material_id IS NOT NULL)
    )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS myscent_formula_event (
    event_id                    varchar(36)  NOT NULL,
    formula_id                  varchar(36)  NOT NULL,
    revision                    bigint       NOT NULL,
    event_type                  varchar(64)  NOT NULL,
    -- formula_created currently has Guid.Empty in the domain and is persisted as NULL.
    -- MySQL unique indexes permit multiple NULL values while still protecting real command IDs.
    command_id                  char(36)     NULL,
    actor_id                    varchar(64)  NOT NULL,
    session_id                  varchar(64)  NULL,
    scene_id                    varchar(64)  NULL,
    before_state_hash           char(64)     NOT NULL,
    after_state_hash            char(64)     NOT NULL,
    change_json                 json         NOT NULL,
    compensates_event_id        varchar(36)  NULL,
    engine_version              varchar(32)  NOT NULL,
    block_library_version       varchar(32)  NOT NULL,
    relation_library_version    varchar(32)  NOT NULL,
    server_time                 datetime(6)  NOT NULL,
    created_at                  datetime(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (event_id),
    UNIQUE KEY uq_event_formula_revision (formula_id, revision),
    UNIQUE KEY uq_event_command (command_id),
    KEY ix_event_formula_time (formula_id, server_time),
    KEY ix_event_compensation (compensates_event_id),
    CONSTRAINT fk_event_formula
        FOREIGN KEY (formula_id) REFERENCES myscent_formula (formula_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_event_compensated_event
        FOREIGN KEY (compensates_event_id) REFERENCES myscent_formula_event (event_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_event_revision_nonnegative CHECK (revision >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS myscent_idempotency_record (
    actor_scope             varchar(128) NOT NULL,
    idempotency_key         char(36)     NOT NULL,
    formula_id              varchar(36)  NOT NULL,
    command_id              char(36)     NOT NULL,
    request_fingerprint     char(64)     NOT NULL,
    response_json           json         NOT NULL,
    result_revision         bigint       NOT NULL,
    created_at              datetime(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    expires_at              datetime(6)  NOT NULL,
    PRIMARY KEY (actor_scope, idempotency_key),
    UNIQUE KEY uq_idempotency_command (command_id),
    KEY ix_idempotency_expiry (expires_at),
    KEY ix_idempotency_formula (formula_id, result_revision),
    CONSTRAINT fk_idempotency_formula
        FOREIGN KEY (formula_id) REFERENCES myscent_formula (formula_id)
        ON DELETE CASCADE,
    CONSTRAINT ck_idempotency_revision_nonnegative CHECK (result_revision >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS myscent_analysis_snapshot (
    analysis_id                 varchar(36)  NOT NULL,
    formula_id                  varchar(36)  NOT NULL,
    formula_revision            bigint       NOT NULL,
    current_revision_at_finish  bigint       NOT NULL,
    freshness_state             varchar(16)  NOT NULL,
    formula_hash                char(64)     NOT NULL,
    numeric_output_hash         char(64)     NOT NULL,
    scene_id                    varchar(64)  NOT NULL,
    engine_version              varchar(32)  NOT NULL,
    block_library_version       varchar(32)  NOT NULL,
    relation_library_version    varchar(32)  NOT NULL,
    prediction_json             json         NOT NULL,
    confidence_score            decimal(7,6) NOT NULL,
    created_at                  datetime(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (analysis_id),
    UNIQUE KEY uq_analysis_input (
        formula_id,
        formula_revision,
        engine_version,
        block_library_version,
        relation_library_version
    ),
    KEY ix_analysis_formula_created (formula_id, created_at),
    CONSTRAINT fk_analysis_formula
        FOREIGN KEY (formula_id) REFERENCES myscent_formula (formula_id)
        ON DELETE CASCADE,
    CONSTRAINT ck_analysis_revisions_nonnegative CHECK (
        formula_revision >= 0 AND current_revision_at_finish >= 0
    ),
    CONSTRAINT ck_analysis_freshness CHECK (freshness_state IN ('fresh', 'stale')),
    CONSTRAINT ck_analysis_confidence CHECK (confidence_score >= 0 AND confidence_score <= 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS myscent_formula_version (
    formula_version_id          varchar(36)  NOT NULL,
    formula_id                  varchar(36)  NOT NULL,
    version_no                  int          NOT NULL,
    source_revision             bigint       NOT NULL,
    title                       varchar(128) NULL,
    label_text                  varchar(128) NULL,
    sealed                      boolean      NOT NULL DEFAULT false,
    formula_hash                char(64)     NOT NULL,
    aggregate_state_hash        char(64)     NOT NULL,
    components_json             json         NOT NULL,
    prediction_snapshot_id      varchar(36)  NULL,
    engine_version              varchar(32)  NOT NULL,
    block_library_version       varchar(32)  NOT NULL,
    relation_library_version    varchar(32)  NOT NULL,
    created_at                  datetime(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (formula_version_id),
    UNIQUE KEY uq_formula_version_no (formula_id, version_no),
    UNIQUE KEY uq_formula_source_revision (formula_id, source_revision),
    KEY ix_formula_version_created (formula_id, created_at),
    CONSTRAINT fk_version_formula
        FOREIGN KEY (formula_id) REFERENCES myscent_formula (formula_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_version_analysis
        FOREIGN KEY (prediction_snapshot_id) REFERENCES myscent_analysis_snapshot (analysis_id)
        ON DELETE SET NULL,
    CONSTRAINT ck_formula_version_numbers CHECK (version_no > 0 AND source_revision >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS myscent_spec_manifest (
    spec_kind               varchar(32)  NOT NULL,
    spec_version            varchar(32)  NOT NULL,
    content_sha256          char(64)     NOT NULL,
    status                  varchar(32)  NOT NULL,
    content_path            varchar(255) NOT NULL,
    is_active               boolean      NOT NULL DEFAULT false,
    loaded_at               datetime(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (spec_kind, spec_version, content_sha256),
    KEY ix_spec_active (spec_kind, is_active),
    CONSTRAINT ck_spec_kind CHECK (
        spec_kind IN ('engine', 'blocks', 'relations', 'command_protocol', 'state_machine')
    )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The migration runner must replace the placeholder checksum with the actual
-- SHA-256 of this file before recording the migration in production.
INSERT INTO myscent_schema_migration (migration_id, checksum_sha256)
VALUES ('0001_p0a_core', REPEAT('0', 64))
ON DUPLICATE KEY UPDATE migration_id = VALUES(migration_id);
