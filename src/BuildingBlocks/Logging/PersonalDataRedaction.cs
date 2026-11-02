using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Logging;

// Redação imposta pelo framework de logging, não por disciplina (ADR-0017): o valor marcado com [PersonalData] sai do
// log como texto vazio, em todo provedor. Api e Worker chamam este mesmo registro (ADR-0041).
public static class PersonalDataRedaction
{
    public static IHostApplicationBuilder AddPersonalDataRedaction(this IHostApplicationBuilder builder)
    {
        builder.Services.AddRedaction(redaction =>
            redaction.SetRedactor<ErasingRedactor>(new DataClassificationSet(PersonalDataTaxonomy.PersonalData)));
        builder.Logging.EnableRedaction();
        return builder;
    }
}
