# Política do AppRole da Api (ADR-0016): cria e lê DEKs, calcula os dois HMACs, e nada mais. Sem `update` na DEK, uma
# escrita sobre DEK existente é recusada; sem `delete`, nenhuma DEK é destruída fora da eliminação.
path "customers/data/dek/*" {
  capabilities = ["create", "read"]
}

path "transit/hmac/customers-blind-index" {
  capabilities = ["update"]
}

path "transit/hmac/customers-request-hash" {
  capabilities = ["update"]
}
