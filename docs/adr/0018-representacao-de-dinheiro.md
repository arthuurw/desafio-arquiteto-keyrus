# ADR-0018: Representação de dinheiro com `decimal` e value object `Money`

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

Todo valor do sistema é monetário. Erros de representação (arredondamento binário, conversão de
escala, perda de precisão na serialização) geram diferenças de centavos que, acumuladas,
tornam-se inconsistências contábeis.

## Alternativas consideradas

1. **`decimal` no C# e `numeric(19,4)` no PostgreSQL.**
   - Prós: aritmética exata em base 10; leitura natural; escala 4 comporta frações de centavo caso
     surjam (juros, câmbio).
   - Contras: operações um pouco mais lentas que inteiros, irrelevante para este sistema.
2. **`long` em centavos.**
   - Prós: exato e rápido; comum em sistemas de pagamento.
   - Contras: conversões espalhadas pelo código (erro clássico de multiplicar ou dividir por 100
     no lugar errado); escala fixa em 2 casas.
3. **`double` ou `float`.** Rejeitada: ponto flutuante binário não representa valores decimais com
   exatidão (`0.1 + 0.2 != 0.3`). Inaceitável para dinheiro.

## Decisão

**Alternativa 1**, sempre encapsulada no value object **`Money`** (valor + moeda):

- **Moeda:** apenas `BRL` nesta versão. Operações entre moedas diferentes são rejeitadas pelo
  próprio `Money`. Multi-moeda é evolução documentada.
- **`Money` aceita valores negativos**, pois representa também saldos, que podem ficar negativos
  até o limite de crédito da conta (ADR-0001). Ex.: saldo `-50.00` em conta com limite `100.00`.
- **O valor de um lançamento (`Entry.Amount`) é sempre estritamente positivo.** O efeito no saldo
  é dado pelo tipo (`Credit` soma, `Debit` subtrai), nunca pelo sinal do valor. Um débito de
  R$ 80 é registrado como `Debit` de `80.00`, e não como `-80.00`. Isso elimina a ambiguidade de
  um "débito negativo" (que somaria ao saldo) e torna o tipo do lançamento a única fonte do
  sentido da movimentação. Um estorno também tem valor positivo, com o tipo oposto ao do
  lançamento estornado.
- **Escala de entrada:** valores recebidos pela API devem ter no máximo 2 casas decimais
  (centavos); valores com mais casas são rejeitados, nunca arredondados silenciosamente.
- **Arredondamento explícito:** quando necessário, apenas em pontos declarados, com
  `MidpointRounding.ToEven` (arredondamento bancário).
- **Serialização JSON como string** (`"150.75"`): números JSON são interpretados como ponto
  flutuante por muitos clientes (ex.: JavaScript), com risco de perda de precisão.
- **Banco:** `numeric(19,4)`; nunca `money` do PostgreSQL, cujo comportamento depende da
  configuração de localidade do servidor.

## Consequências

- **Positivas:**
  - Nenhum erro de arredondamento binário em qualquer camada.
  - Regras monetárias concentradas e testadas em um único tipo.
- **Negativas:**
  - Clientes da API precisam enviar e interpretar valores como string; documentado na OpenAPI.
- **Evolução:**
  - Multi-moeda com escala por moeda (ISO 4217) e conversão explícita com taxa registrada.
