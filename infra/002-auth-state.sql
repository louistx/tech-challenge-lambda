-- Apply once with a migration/owner role, after agreement with the database team.
-- Runtime role needs no CREATE/ALTER privileges. No foreign key to a cross-team view.
CREATE TABLE IF NOT EXISTS auth_challenges (
 id varchar(36) PRIMARY KEY,
 client_id varchar(36) NOT NULL,
 verifier varchar(64) NOT NULL,
 expires_at bigint NOT NULL,
 attempts integer NOT NULL DEFAULT 0,
 consumed boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_auth_challenges_expires_at ON auth_challenges(expires_at);
CREATE TABLE IF NOT EXISTS auth_rate_limits (
 key varchar(64) NOT NULL,
 "window" bigint NOT NULL,
 count integer NOT NULL,
 PRIMARY KEY (key, "window")
);
