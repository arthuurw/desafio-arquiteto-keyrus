# Melhorias futuras

Evoluções identificadas e conscientemente deixadas fora do escopo inicial. Cada item aponta o ADR
onde o contexto está registrado.

| Melhoria | Motivação | Origem |
|----------|-----------|--------|
| Modelo bitemporal (data de registro + data de competência) | Representar lançamentos com data-valor retroativa (ajustes contábeis, operações agendadas) | [ADR-0002](adr/0002-modelo-temporal-tempo-de-registro.md) |
| Sub-ledgers para contas quentes | Reduzir contenção de escrita em contas com altíssimo volume | [ADR-0003](adr/0003-saldo-materializado-e-saldo-acumulado-por-lancamento.md) |
| Event sourcing / event store | Múltiplas projeções sobre o histórico (extratos, regulatório, antifraude) | [ADR-0003](adr/0003-saldo-materializado-e-saldo-acumulado-por-lancamento.md) |
| Job de reconciliação periódica | Recalcular saldos a partir dos lançamentos e alertar divergências | [ADR-0003](adr/0003-saldo-materializado-e-saldo-acumulado-por-lancamento.md) |
| Sharding da escrita por `account_id` | Escalar escrita horizontalmente além de um nó primário | [ADR-0004](adr/0004-postgresql-como-banco-de-dados.md) |
| mTLS entre serviços e tokens vinculados a certificado (RFC 8705) | Defesa em profundidade: token vazado inútil sem o certificado | [ADR-0012](adr/0012-autenticacao-oauth2-client-credentials.md) |
| Cofre gerenciado (Azure Key Vault, AWS KMS, Google Cloud KMS) e cluster OpenBao em alta disponibilidade | Operação gerenciada ou HA do cofre em produção | [ADR-0016](adr/0016-criptografia-de-dados-pessoais.md) |
| Multi-moeda (ISO 4217) | Escala por moeda e conversão com taxa registrada | [ADR-0018](adr/0018-representacao-de-dinheiro.md) |
| SLOs formais e tail sampling | Alertas por orçamento de erro; reter 100% dos traces com erro | [ADR-0022](adr/0022-observabilidade-com-opentelemetry.md) |
| Mutação nos demais módulos | Estender o Stryker além do domínio do Ledger | [ADR-0023](adr/0023-estrategia-de-testes.md) |
| Message broker entre outbox e projetores (RabbitMQ ou Kafka) | Desacoplar publicação de consumo, permitir múltiplos consumidores (antifraude, notificações, contabilidade), *backpressure* e replay | [ADR-0006](adr/0006-cqrs-com-modelo-de-leitura-separado.md) |
