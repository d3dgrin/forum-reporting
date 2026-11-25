-- One PostgreSQL server, one database per service (database-per-service ownership).
-- Reporting.Api owns reporting_db; Account.Api owns account_db. Neither touches the other's.
CREATE DATABASE reporting_db;
CREATE DATABASE account_db;
