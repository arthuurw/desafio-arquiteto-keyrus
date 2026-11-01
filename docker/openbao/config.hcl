# OpenBao em modo servidor (ADR-0016): storage em volume e auto-unseal estático. O modo `-dev` guarda tudo em
# memória, e um reinício perderia todas as DEKs.
disable_mlock = true
ui            = false

# `/openbao/file` já pertence ao usuário da imagem; o volume montado ali herda o dono.
storage "raft" {
  path    = "/openbao/file"
  node_id = "openbao-1"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true
}

api_addr     = "http://127.0.0.1:8200"
cluster_addr = "http://127.0.0.1:8201"

# Chave de 32 bytes vinda do ambiente: o cofre se deslacra sozinho a cada subida.
seal "static" {
  current_key_id = "local-1"
  current_key    = "env://BAO_STATIC_UNSEAL_KEY"
}
