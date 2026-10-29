-- Roles e schemas por módulo (ADR-0015).
-- Executado pelo superusuário na primeira subida do container, com volume vazio.
-- SENHAS EXCLUSIVAS DO AMBIENTE LOCAL E DOS TESTES: nunca usar fora dele.

CREATE ROLE migrator LOGIN PASSWORD 'migrator_dev';
CREATE ROLE ledger_app LOGIN PASSWORD 'ledger_app_dev';
CREATE ROLE customers_app LOGIN PASSWORD 'customers_app_dev';

-- Ninguém além das roles do sistema conecta; ninguém cria objetos em public.
DO $$
BEGIN
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO migrator, ledger_app, customers_app', current_database());
END
$$;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- O migrator é dono dos schemas: aplica DDL só neles, sem privilégio administrativo.
CREATE SCHEMA ledger AUTHORIZATION migrator;
CREATE SCHEMA customers AUTHORIZATION migrator;

-- Cada role de aplicação alcança só o próprio schema, e só com DML sem DELETE.
GRANT USAGE ON SCHEMA ledger TO ledger_app;
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA ledger
    GRANT SELECT, INSERT, UPDATE ON TABLES TO ledger_app;
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA ledger
    GRANT USAGE, SELECT ON SEQUENCES TO ledger_app;

GRANT USAGE ON SCHEMA customers TO customers_app;
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA customers
    GRANT SELECT, INSERT, UPDATE ON TABLES TO customers_app;
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA customers
    GRANT USAGE, SELECT ON SEQUENCES TO customers_app;

-- Funções do migrator não são executáveis por padrão: uma função SECURITY DEFINER contornaria o
-- DML restrito. Quem precisar executar recebe GRANT EXECUTE na migração que cria a função.
ALTER DEFAULT PRIVILEGES FOR ROLE migrator REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
