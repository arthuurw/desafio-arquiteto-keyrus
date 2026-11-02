using BuildingBlocks.Logging;

namespace FixtureGamma.Contracts;

// Contrato fora de Customers com um membro classificado como dado pessoal: a regra de fronteira o aponta.
public sealed record ContactSnapshot(Guid Id, [property: PersonalData] string Phone);
