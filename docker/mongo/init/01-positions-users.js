// Usuários do modelo de leitura (ADR-0035): a Api só lê o banco `positions`, o Worker lê e grava.
// Executado pelo usuário root na primeira subida do container, com volume vazio.
// SENHAS EXCLUSIVAS DO AMBIENTE LOCAL E DOS TESTES: nunca usar fora dele.
const positions = db.getSiblingDB("positions");
positions.createUser({
    user: "positions_reader",
    pwd: "positions_reader_dev",
    roles: [{ role: "read", db: "positions" }],
});
positions.createUser({
    user: "positions_writer",
    pwd: "positions_writer_dev",
    roles: [{ role: "readWrite", db: "positions" }],
});
