using Microsoft.Extensions.Compliance.Classification;

namespace BuildingBlocks.Logging;

// Classificação de dado pessoal: nome, CPF, nascimento e e-mail do cliente (ADR-0017, ADR-0041).
public static class PersonalDataTaxonomy
{
    public const string Name = "Ledger";

    public static DataClassification PersonalData { get; } = new(Name, nameof(PersonalData));
}

// Marca o parâmetro de log, ou o membro, que carrega dado pessoal: no log, o valor sai apagado.
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class PersonalDataAttribute : DataClassificationAttribute
{
    public PersonalDataAttribute()
        : base(PersonalDataTaxonomy.PersonalData)
    {
    }
}
