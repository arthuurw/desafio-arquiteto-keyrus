# ADR-0011: Convenção de idioma

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

O enunciado e o negócio estão em português, enquanto o ecossistema .NET (bibliotecas, convenções,
nomes de padrões) é em inglês. Misturar idiomas no código (ex.: `Features/ConsultarSaldo`,
`GetSaldoAsync`) produz nomes híbridos difíceis de ler e de buscar.

## Decisão

| Artefato | Idioma |
|----------|--------|
| Código: namespaces, tipos, membros, pastas, banco de dados, eventos | Inglês |
| Comentários no código (curtos, apenas quando o "porquê" não é óbvio) | Português |
| Documentação do repositório (README, ADRs, guias) | Português |
| Documentação da API (OpenAPI: descrições, exemplos, mensagens de erro) | Português |

A linguagem ubíqua é mantida nos dois idiomas por meio do [glossário](../glossario.md), que mapeia
cada termo de negócio ao seu nome no código. Termos sem tradução fiel (ex.: CPF) mantêm a forma
original.

## Consequências

- **Positivas:**
  - Código consistente com o ecossistema e com bibliotecas de terceiros.
  - Documentação acessível ao negócio e ao time local.
- **Negativas:**
  - Necessidade de manter o glossário atualizado para que os nomes do código continuem
    rastreáveis aos termos de negócio.
