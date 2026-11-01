using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace IntegrationTests.Infrastructure;

// MongoDB real, inicializado pelo mesmo script de usuários do docker-compose.
internal sealed class MongoReadModel : IAsyncDisposable
{
    internal const string Image = "mongo:8.0.34";
    internal const string Reader = "positions_reader";
    internal const string Writer = "positions_writer";

    private readonly MongoDbContainer _container = new MongoDbBuilder(Image)
        .WithUsername("admin")
        .WithPassword("admin_dev")
        .WithResourceMapping(new FileInfo(Repository.MongoInitScript), "/docker-entrypoint-initdb.d/")
        .Build();

    private MongoClient _admin = null!;

    // Coleções vistas como administrador: o estado observável da projeção.
    internal IMongoCollection<BsonDocument> Positions => _admin.GetDatabase("positions").GetCollection<BsonDocument>("positions");

    internal IMongoCollection<BsonDocument> Entries => _admin.GetDatabase("positions").GetCollection<BsonDocument>("entries");

    internal static async Task<MongoReadModel> StartAsync()
    {
        var mongo = new MongoReadModel();
        await mongo._container.StartAsync(TestContext.Current.CancellationToken);
        mongo._admin = new MongoClient(mongo._container.GetConnectionString());
        await mongo.WaitForUsersAsync();
        return mongo;
    }

    internal string ConnectionStringFor(string user) =>
        $"mongodb://{user}:{user}_dev@{_container.Hostname}:{_container.GetMappedPublicPort(MongoDbBuilder.MongoDbPort)}/positions?authSource=positions";

    // Pausado, o MongoDB aceita a conexão TCP e não responde: o caso do timeout, não o da conexão recusada.
    // Com o Docker carregado, congelar o cgroup às vezes falha ("unable to freeze") e passa na tentativa seguinte.
    internal async Task<IAsyncDisposable> PauseAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _container.PauseAsync(TestContext.Current.CancellationToken);
                return new Unpause(_container);
            }
            catch (Docker.DotNet.DockerApiException exception) when (attempt < 5 && exception.Message.Contains("unable to freeze", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            }
        }
    }

    internal async Task<BsonDocument?> PositionAsync(Guid accountId) =>
        await Positions.Find(new BsonDocument("_id", Uuid(accountId))).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

    internal async Task<List<BsonDocument>> EntriesAsync(Guid accountId) =>
        await Entries.Find(new BsonDocument("accountId", Uuid(accountId)))
            .Sort(new BsonDocument("sequence", 1))
            .ToListAsync(TestContext.Current.CancellationToken);

    internal static BsonBinaryData Uuid(Guid id) => new(id, GuidRepresentation.Standard);

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    // O script de init roda num mongod temporário, depois da porta já responder: espera o usuário de escrita autenticar.
    private async Task WaitForUsersAsync() =>
        await Eventually.WaitUntilAsync(
            async () =>
            {
                try
                {
                    using var client = new MongoClient(ConnectionStringFor(Writer));
                    await client.GetDatabase("positions").RunCommandAsync<BsonDocument>(
                        new BsonDocument("ping", 1), cancellationToken: TestContext.Current.CancellationToken);
                    return true;
                }
                catch (MongoException)
                {
                    return false;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(60),
            () => "positions_writer never authenticated",
            TimeSpan.FromMilliseconds(250));

    private sealed class Unpause(MongoDbContainer container) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await container.UnpauseAsync(TestContext.Current.CancellationToken);
    }
}
