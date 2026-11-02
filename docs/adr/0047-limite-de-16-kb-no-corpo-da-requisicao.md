# ADR-0047: Limite de 16 KB no corpo de toda requisição à Api

- **Status:** Aceita
- **Data:** 2026-11-02
- **Complementa:** ADR-0021

## Contexto

O Kestrel aceita, por padrão, corpos de até 30 MB. Com esse padrão, cada requisição à Api pode
alocar e parsear até 30 MB antes de a validação da rota recusar o JSON. O maior corpo válido da
Api tem menos de 1 KB. Um corpo grande ocupa memória e CPU da mesma Api que registra as
movimentações, que é o que o ADR-0021 protege sob carga.

## Alternativas consideradas

1. **Manter o padrão do Kestrel (30 MB).** Rejeitada: o custo de uma requisição fica 30 mil
   vezes acima do maior corpo legítimo.
2. **Limite por rota, no endpoint.** Rejeitada: uma rota nova sem o limite volta ao padrão, e o
   parse do JSON já aconteceu quando o endpoint decide.
3. **Um limite para toda a Api, antes do endpoint e do parse.**

## Decisão

**Alternativa 3.**

- O corpo acima de 16 384 bytes recebe `413` Problem Details `payload-too-large` sem que o JSON
  seja lido. Até 16 384 bytes, o corpo segue para a validação da rota.
- Vale para toda requisição, também para uma rota que ainda não existe.
- Com `Content-Length`, o tamanho declarado decide. Sem ele (envio em chunks), a Api lê até um
  byte além do limite, e o que coube segue para o endpoint no lugar do corpo original.
- O limite roda depois da autenticação e da autorização: o `401` e o `403` continuam como são, e a
  trilha de auditoria registra o client do `413` (ADR-0041).
- O `413` não consome a `Idempotency-Key`: a mesma chave com um corpo válido é aceita como nova.

## Consequências

- **Positivas:**
  - O custo de uma requisição fica limitado a 16 KB de corpo, em toda rota, sem configuração por
    endpoint.
  - O `413` aparece na OpenAPI de toda rota com corpo, e um teste confere o documento servido.
- **Negativas:**
  - Uma rota futura que precise de corpo maior, como um upload, pede um ADR novo que mude a regra.
  - O envio em chunks é lido por inteiro, até 16 KB, antes do endpoint.
