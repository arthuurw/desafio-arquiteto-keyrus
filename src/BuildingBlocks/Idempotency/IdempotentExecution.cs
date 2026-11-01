using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BuildingBlocks.Idempotency;

// Executa um comando idempotente uma vez por (client, chave): chave, operação e resposta confirmam na mesma transação,
// então não existe operação feita sem chave registrada, nem o inverso (ADR-0037). O módulo informa o `DbContext`, a
// tabela de chaves do schema dele e como abrir a transação de escrita.
public sealed class IdempotentExecution(DbContext db, string keysTable, Func<CancellationToken, Task<IDbContextTransaction>> beginTransaction)
{
    // `hash` já calculado pelo módulo, antes de qualquer acesso ao banco.
    public async Task<WriteResult> ExecuteOnceAsync(
        IIdempotentRequest request, byte[] hash, Func<Task<WriteResult>> execute, CancellationToken cancellationToken)
    {
        var clientId = request.Caller.ClientId;
        var key = request.IdempotencyKey;
        await using var transaction = await beginTransaction(cancellationToken);

        var stored = await ClaimKeyAsync(clientId, key, hash, cancellationToken);
        if (stored is not null)
        {
            return stored.RequestHash.AsSpan().SequenceEqual(hash)
                ? new WriteResult(stored.Status, stored.Body) { Replayed = true }
                : WriteResult.Problem(IdempotencyProblems.KeyReused());
        }

        var result = await execute();
        if (result.Transient)
        {
            // Sem commit: o descarte da transação desfaz a chave junto, e a repetição executa de novo.
            return result;
        }

        await db.Database.ExecuteSqlAsync(
            Sql(
                $"UPDATE {keysTable} SET response_status = {{0}}, response_body = {{1}}::jsonb "
                    + "WHERE client_id = {2} AND idempotency_key = {3}",
                result.Status, result.Body, clientId, key),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    // Registra a chave nesta transação e devolve null, ou devolve a resposta já confirmada com ela.
    private async Task<StoredResponse?> ClaimKeyAsync(string clientId, Guid key, byte[] hash, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Com a mesma chave numa transação aberta, o PostgreSQL faz este INSERT esperar o desfecho dela:
            // confirmada, nada é inserido e vale a resposta dela; desfeita, a inserção prossegue.
            var inserted = await db.Database.ExecuteSqlAsync(
                Sql(
                    $"INSERT INTO {keysTable} (client_id, idempotency_key, request_hash) VALUES ({{0}}, {{1}}, {{2}}) "
                        + "ON CONFLICT DO NOTHING",
                    clientId, key, hash),
                cancellationToken);
            if (inserted == 1)
            {
                return null;
            }

            var stored = await db.Database
                .SqlQuery<StoredResponse>(
                    Sql(
                        "SELECT request_hash AS \"RequestHash\", response_status AS \"Status\", response_body::text AS \"Body\" "
                            + $"FROM {keysTable} WHERE client_id = {{0}} AND idempotency_key = {{1}}",
                        clientId, key))
                .SingleOrDefaultAsync(cancellationToken);
            // A limpeza pode apagar a chave entre o conflito e esta leitura: a chave é registrada de novo, e a
            // operação executa como nova.
            if (stored is not null)
            {
                return stored;
            }
        }
    }

    // A tabela é um identificador fixo do módulo, nunca entrada; os valores seguem como parâmetros.
    private static FormattableString Sql(string format, params object[] arguments) =>
        FormattableStringFactory.Create(format, arguments);

    // Só chave confirmada é encontrada aqui, e ela sempre tem resposta.
    private sealed record StoredResponse(byte[] RequestHash, int Status, string Body);
}
