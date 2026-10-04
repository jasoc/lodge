-- A kind onboarded onto Lodge. Multi-kind by design.
CREATE TABLE IF NOT EXISTS kinds (
    code    varchar(64)  PRIMARY KEY,
    name    varchar(256) NOT NULL,
    enabled boolean      NOT NULL DEFAULT false
);
