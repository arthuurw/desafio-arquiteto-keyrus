# Glossário (linguagem ubíqua)

Correspondência entre os termos de negócio (português) e os nomes usados no código (inglês),
conforme o [ADR-0011](adr/0011-convencao-de-idioma.md).

## Ledger

| Termo de negócio | Código | Definição |
|------------------|--------|-----------|
| Conta | `Account` | Agregado que mantém saldo, limite e versão. Fronteira de consistência das movimentações. |
| Lançamento | `Entry` | Registro imutável de uma movimentação em uma conta. Nunca é alterado ou excluído. |
| Crédito | `Credit` | Lançamento que aumenta o saldo. |
| Débito | `Debit` | Lançamento que diminui o saldo. Sujeito ao limite. |
| Estorno | `Reversal` | Lançamento que anula o efeito de um lançamento anterior, com sinal oposto. |
| Limite de crédito | `CreditLimit` | Valor não negativo até o qual o saldo pode ficar negativo. Padrão 0. |
| Saldo | `Balance` | Valor consolidado da conta. |
| Dinheiro | `Money` | Value object com valor (`decimal`) e moeda. Apenas BRL nesta versão (ADR-0018). |
| Saldo após o lançamento | `BalanceAfter` | Saldo acumulado da conta imediatamente após um lançamento. |
| Momento de registro | `RecordedAt` | Instante, atribuído pelo servidor, em que o lançamento foi gravado. |
| Sequência | `Sequence` | Número de ordem do lançamento dentro da conta. |
| Versão | `Version` | Contador incrementado a cada lançamento na conta; usado na projeção e na verificação de frescor. |
| Categoria | `Category` | Tipo codificado do lançamento (ex.: `PIX_IN`, `TED_OUT`). Lançamentos não têm descrição livre (ADR-0017). |
| Referência externa | `ExternalReference` | Identificador da operação no sistema de origem; único por client (ADR-0014). |

## Positions

| Termo de negócio | Código | Definição |
|------------------|--------|-----------|
| Posição consolidada | `Position` | Visão de leitura do saldo de uma conta. |
| Saldo em um momento | `BalanceAt` | Saldo da conta em um instante T: `BalanceAfter` do último lançamento com `RecordedAt <= T`. |
| Extrato | `Statement` | Lista de lançamentos de uma conta em um intervalo. |

## Customers

| Termo de negócio | Código | Definição |
|------------------|--------|-----------|
| Cliente | `Customer` | Pessoa titular de contas. Detém os dados pessoais. |
| CPF | `Cpf` | Documento de identificação do cliente. Dado pessoal, armazenado criptografado. |

## Identidade

| Termo de negócio | Código | Definição |
|------------------|--------|-----------|
| Chamador | `Caller` | Quem chama a Api: o client e, no modo delegado, o cliente final em nome de quem ele age (ADR-0013). |
| Modo delegado | `Delegated` | Canal agindo em nome do cliente logado; o token carrega `customer_id` e não carrega `client_id`. |
| Modo serviço | `Service` | Sistema agindo por conta própria, sem usuário; o token carrega `client_id` e não carrega `customer_id`. |
| Escopo | `Scope` | Capacidade concedida a um client, como `ledger.credits.write`; cada rota exige exatamente um. |

## Integração

| Termo de negócio | Código | Definição |
|------------------|--------|-----------|
| Lançamento registrado | `EntryRecorded` | Evento de integração publicado pelo Ledger via outbox. |
