namespace FixtureAlpha.Features;

// Fixture da regra de fronteira de dados pessoais: um contrato que carrega o CPF para fora do módulo dono.
internal sealed record CustomerSnapshot(Guid Id, string Cpf);
