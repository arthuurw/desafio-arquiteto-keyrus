# Testes explicados

Guia didático de como os testes deste projeto garantem que o sistema faz o que promete. As
decisões formais estão no [ADR-0023](../adr/0023-estrategia-de-testes.md) e no
[ADR-0034](../adr/0034-processo-spec-driven-e-codigo-como-fonte-da-verdade.md); as regras
operacionais, no [`AGENTS.md`](../../AGENTS.md).

**Situação:** os testes de domínio, propriedades, integração, concorrência, falha do modelo de
leitura, segurança de acesso, arquitetura, build, smoke e mutação existem hoje. São previstos: a
falha do OpenBao (F5), a ausência de dados pessoais nos logs (F6) e o teste de carga (F9).

## 1. O problema: o teste que mente

Um teste verde significa "o código fez o que o teste verificou". Não significa "o código está
certo". A distância entre as duas frases é onde moram os testes que mentem:

```csharp
// Parece testar o limite de saldo. Não testa nada.
[Fact]
public async Task Debit_beyond_limit_is_rejected()
{
    var response = await client.PostAsync("/v1/accounts/1/debits", body);
    if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}
```

Se o sistema aceitar o débito indevido (`201`), o `if` não entra, nenhuma asserção roda, e o teste
**passa**. Esse é o pior tipo de falha: dá confiança justamente onde não deveria.

Outros disfarces comuns:

| Disfarce | Por que mente |
|----------|---------------|
| Asserção dentro de `if` ou depois de `return` | Pode nunca executar |
| `try/catch` em volta da asserção | Engole a falha |
| Verificar que um método foi chamado, em vez do resultado | Prova a implementação, não o comportamento |
| Banco em memória no lugar do PostgreSQL | Não tem `FOR UPDATE`, constraints nem isolamento: a concorrência nunca é testada de verdade |
| Teste de concorrência com `Thread.Sleep` | As requisições podem nunca chegar a disputar o mesmo recurso |
| Teste marcado como ignorado | Simplesmente não roda |

Toda a estratégia existe para fechar essas portas.

## 2. Os tipos de teste, do mais rápido ao mais completo

### Unitários de domínio

Testam as regras de negócio isoladas, sem banco nem rede: o `Money` só aceita BRL na criação,
a `Account` recusa débito acima do limite, o estorno inverte o tipo do lançamento.

Rodam em milissegundos e apontam exatamente qual regra quebrou.

### Baseados em propriedades

Em vez de escrever exemplos à mão ("crédito de 100, débito de 30, saldo 70"), descrevemos uma
**propriedade** que precisa valer sempre, e a ferramenta (FsCheck) gera mil casos aleatórios por
propriedade tentando quebrá-la.

> **Analogia:** em vez de testar uma ponte com cinco caminhões escolhidos por você, deixar um
> gerador mandar milhares de combinações de peso, velocidade e posição.

Propriedades deste projeto:

- Para **qualquer** sequência de créditos e débitos, o saldo nunca fica abaixo de `-limite`.
- Para **qualquer** sequência, `balance_after` de cada lançamento é o anterior mais o efeito dele.

Quando a ferramenta encontra um caso que quebra a propriedade, ela o **reduz** ao menor exemplo
possível ("débito de 0,01 com saldo 0 e limite 0"), o que facilita muito achar a causa.

### Integração

Sobem a Api de verdade e conversam com ela por HTTP, contra **PostgreSQL, MongoDB e Keycloak
reais**, rodando em containers criados pelo próprio teste (Testcontainers). O Worker e o Migrator
rodam como processos reais, do mesmo jeito que no docker-compose. O OpenBao entra com a
funcionalidade F5.

Por que real e não simulado: os riscos mais graves deste sistema (dois débitos simultâneos, retry
duplicado, leitura defasada) dependem do comportamento exato do PostgreSQL: o lock de linha, a
constraint única, o relógio do servidor. Um banco simulado não tem nada disso, e o teste passaria
sem provar a proteção.

### Concorrência

Disparam N requisições **ao mesmo tempo** contra a mesma conta e verificam o resultado final.

Exemplo: conta com R$ 1.000 e limite 0; 50 débitos simultâneos de R$ 30. Resultado obrigatório:
exatamente 33 aceitos, 17 recusados com `insufficient-funds`, saldo final R$ 10.

O ponto delicado é garantir que as requisições realmente disparem juntas. Usamos uma **barreira**:
todas as tarefas são preparadas e ficam esperando um sinal; o sinal é dado uma única vez e todas
partem no mesmo instante. Com `Thread.Sleep`, a primeira poderia terminar antes de a última
começar, e não haveria concorrência nenhuma.

### Falha

Provocam a falha de propósito durante o teste:

| Falha provocada | O que o teste prova |
|-----------------|---------------------|
| MongoDB parado no meio do teste | A consulta de saldo continua correta, vinda do PostgreSQL |
| MongoDB parado com o Worker rodando | O Worker continua vivo, os eventos esperam no outbox e são projetados quando o MongoDB volta |
| Worker interrompido entre aplicar o evento e marcá-lo como processado | Ao reprocessar, nenhum lançamento aparece duplicado no extrato |
| Evento malformado, ou um lote inteiro de eventos que nunca aplicam | Os outros eventos continuam sendo projetados |
| Dois Workers ao mesmo tempo | Cada lançamento é projetado uma única vez, na ordem da conta |
| OpenBao fora do ar (previsto, F5) | Débitos e consultas de saldo continuarão funcionando |

### Segurança

| Ataque simulado | Resultado obrigatório |
|-----------------|-----------------------|
| Canal tenta ler a conta de outro cliente | `404` (sem revelar que a conta existe) |
| Client sem o escopo necessário | `403` |
| `payments-hub` tenta debitar | `403` |
| Qualquer cenário de teste (previsto, F6) | Nenhum CPF, nome ou e-mail nos logs capturados |

### Arquitetura

Testes que leem o código compilado e verificam regras de estrutura: um módulo não usa nada interno
de outro; o Positions chega ao Ledger só pelo contrato público, e o Ledger não conhece o MongoDB;
cada módulo expõe um único tipo público; o domínio não depende de infraestrutura; todo endpoint
declara uma política de autorização; todo comando de escrita do Ledger exige `Idempotency-Key`,
exceto a mudança de limite.
Se alguém quebrar uma fronteira, os testes de arquitetura falham, e o CI reprova a mudança.

Quase toda regra também é testada contra um módulo de mentira (`tests/Fixtures`) que a viola de
propósito: a regra precisa acusar a violação. Sem isso, uma regra que nunca acusa nada passaria sempre.

### Build

Os testes de build (`tests/BuildTests`) rodam o `dotnet` de verdade sobre pequenos projetos de
prova (`tests/Probes`), cada um com um defeito plantado. O build de cada prova precisa falhar:

| Prova | Defeito plantado |
|-------|------------------|
| `WarningProbe` | Código que gera warning do compilador ou de analyzer |
| `StyleProbe` | Código fora das regras de estilo do `.editorconfig` |
| `VulnerablePackageProbe` | Pacote com vulnerabilidade conhecida |
| `LowSeverityPackageProbe` | Pacote com vulnerabilidade de baixa severidade |
| `InlinePackageVersionProbe` | Versão de pacote escrita no projeto, fora do arquivo central |

Duas outras provas cobrem os testes que não rodam. Um teste marcado como ignorado faz a execução
falhar (`failSkips` em `tests/xunit.runner.json`), e um teste marcado como explícito roda sempre
(`--explicit on` em `tests/Directory.Build.targets`). `SkippedTestProbe` e `ExplicitTestProbe`
provam as duas regras.

### Smoke

Scripts em `tests/smoke` sobem o `docker compose` a partir de um clone limpo, como faria quem acabou
de baixar o repositório, e verificam:

- Api e Worker respondem saudáveis, e o Keycloak serve o realm `ledger`;
- com o Migrator falhando, Api e Worker não sobem;
- o PostgreSQL tem a versão esperada;
- um crédito feito pela Api é projetado pelo Worker no banco `positions` do MongoDB.

### Carga (previsto, F9)

Um script k6 vai simular muitos chamadores simultâneos e medir quantos débitos por segundo o sistema
aguenta, a latência dos piores casos (p99) e se as escritas continuam saudáveis quando as consultas
explodem. Os números ficarão registrados no repositório.

## 3. Mutação: o teste dos testes

Esta é a peça que transforma "acho que meus testes são bons" em fato verificável.

A ferramenta (Stryker.NET) faz pequenas alterações **propositais** no código, chamadas
**mutantes**, e roda os testes para cada uma:

| Código original | Mutante |
|-----------------|---------|
| `if (newBalance < -creditLimit)` | `if (newBalance <= -creditLimit)` |
| `return balance + amount;` | `return balance - amount;` |
| `version++;` | *(linha removida)* |

Para cada mutante:

- **Algum teste falhou:** o mutante foi **morto**. Os testes perceberam a alteração. Ótimo.
- **Todos os testes passaram:** o mutante **sobreviveu**. Existe uma versão errada do código que os
  testes aceitam. Ou falta um teste, ou um teste existente não verifica o que deveria.

> **Analogia:** um alarme de incêndio só é confiável se for testado com fumaça de verdade. A
> mutação é a fumaça: se o alarme não toca, ele não protege nada.

O exemplo do início deste guia (asserção dentro do `if`) seria pego imediatamente: o mutante que
aceita o débito indevido sobreviveria.

Neste projeto, a mutação roda em dois momentos. No CI, o Stryker.NET muta só os arquivos de
`Domain` do Ledger, roda contra eles apenas os testes de `Ledger.UnitTests` e reprova a mudança
quando menos de 80% dos mutantes morrem. Na verificação de cada entrega, o
verificador injeta as próprias mutações onde as regras vivem, e uma que nenhum teste mata **reprova
a entrega**.

## 4. Como os testes nascem: spec-driven

Cada funcionalidade passa por quatro etapas:

```
PLAN → CHECKS → BUILD → VERIFY
```

1. **Plan:** o que precisa ser verdade, com critérios de aceite. Exemplo: *"QUANDO um débito
   deixar o saldo abaixo de -limite, o sistema DEVE recusar com 422 `insufficient-funds` e não
   registrar lançamento."*
2. **Checks:** cada critério vira afirmações observáveis, cada uma com o nome do teste que a prova.
   Uma tabela liga cada caso possível (cada status de cada rota) a um teste. Caso sem teste
   aparece como célula vazia, impossível de passar despercebido.
3. **Build:** os testes são escritos **a partir dos checks**, nunca lendo a implementação. Quem lê
   a implementação para escrever o teste acaba testando o que o código faz, e não o que ele
   deveria fazer.
4. **Verify:** um **verificador independente**, que não participou da construção, roda todos os
   testes, localiza no código a linha de cada asserção, confere a tabela de cobertura e injeta
   mutantes. Só ele declara a entrega pronta.

Por que o verificador precisa ser outro: quem construiu revisa com as mesmas suposições que
produziram o erro. Um olhar novo não carrega essas suposições.

## 5. O código é a fonte da verdade

A especificação orienta a construção, mas depois de entregue quem define o comportamento é o
código, e os **testes são a especificação executável**: se alguém quiser saber o que acontece
quando um débito excede o limite, o teste responde, e responde sempre a verdade atual, porque roda
a cada build.

Documentos escritos à mão envelhecem. Por isso, o que pode ser derivado do código (contrato da API,
regras de fronteira) é gerado ou verificado automaticamente, e o que não pode (o porquê das
decisões) fica nos ADRs.
