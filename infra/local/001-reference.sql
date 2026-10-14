-- Local reference only. Never run against the group's production database.
CREATE TABLE auth_clients (
    id varchar(36) PRIMARY KEY,
    cpf varchar(11) NOT NULL UNIQUE,
    active boolean NOT NULL,
    email varchar(254) NOT NULL,
    created_at bigint NOT NULL,
    updated_at bigint NOT NULL
);
INSERT INTO auth_clients VALUES
('00000000-0000-4000-8000-000000000001', '52998224725', true, 'cliente@example.test',
 extract(epoch FROM now())::bigint, extract(epoch FROM now())::bigint),
('00000000-0000-4000-8000-000000000002', '11144477735', false, 'inativo@example.test',
 extract(epoch FROM now())::bigint, extract(epoch FROM now())::bigint);
