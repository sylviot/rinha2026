# rinha2026 — Fraud detection API (.NET 9 AOT)

Implementação para a Rinha de Backend 2026 (detecção de fraude por busca vetorial).

## Stack

- .NET 9 com Native AOT (`PublishAot=true`)
- Kestrel + Minimal API + System.Text.Json source generator
- AVX2 brute-force KNN sobre dataset quantizado em int8, mmap em ambas as instâncias
- 2 instâncias da API + nginx em round-robin como load balancer

## Layout

```
docker-compose.yml      # lb + api1 + api2, todos com limites de recursos
nginx.conf              # upstream round-robin para api1/api2
Dockerfile              # multi-stage AOT build (linux-x64)
data/                   # bind-mount: references.json.gz, mcc_risk.json, normalization.json
                        # gera refs.bin e ready no primeiro boot
src/FraudApi/
  ├─ FraudApi.csproj
  ├─ Program.cs         # bootstrap Kestrel + endpoints /ready e /fraud-score
  ├─ Json.cs            # DTOs + JsonSerializerContext
  ├─ Featurize.cs       # payload -> vetor int8 de 14 dims
  ├─ Dataset.cs         # mmap reader + KNN top-5 AVX2
  └─ Preprocess.cs      # references.json.gz -> refs.bin (com lock entre instâncias)
```

## Como rodar localmente

1. Baixe o dataset oficial do desafio em `./data/`:
   - `data/references.json.gz`
   - `data/mcc_risk.json`
   - `data/normalization.json` (opcional — constantes hard-coded conforme spec)

   O repositório do desafio tem um `generate-data.sh` que produz esses arquivos.

2. Suba:

   ```
   docker compose up --build
   ```

   Na primeira execução, **uma** das APIs vai construir `data/refs.bin` (~50 MB) e
   gravar `data/ready`. A outra fica em polling até esse flag aparecer.
   Enquanto não estiver pronto, `GET /ready` responde 503.

3. Teste:

   ```
   curl -i http://localhost:9999/ready
   curl -X POST http://localhost:9999/fraud-score \
        -H 'content-type: application/json' \
        -d '{"id":"tx-1","transaction":{"amount":300,"installments":1,"requested_at":"2026-03-11T20:23:35Z"},"customer":{"avg_amount":500,"tx_count_24h":2,"known_merchants":["MERC-001"]},"merchant":{"id":"MERC-001","mcc":"5411","avg_amount":250},"terminal":{"is_online":true,"card_present":true,"km_from_home":5.0},"last_transaction":null}'
   ```

## Decisões de design

- **Quantização int8 por dimensão** (`v * 127`, com `-127` reservado para sentinel `-1`
  dos campos `minutes_since_last_tx` e `km_from_last_tx`).
- **Stride de 16 bytes por vetor** (14 dims + 2 bytes de padding zero) para alinhar
  `Vector128<sbyte>` / `Vector256<short>` sem precisar mascarar.
- **Distância L2² inteira** via `vpmaddwd` (`Avx2.MultiplyAddAdjacent`), acumulada em
  `Vector256<int>`. Sem `sqrt` — KNN só precisa da ordem.
- **Top-5 com 5 variáveis em ordem** — mais barato que `PriorityQueue`/heap para k=5.
- **Preprocess single-writer** com lock de arquivo (`/data/.lock`) + flag (`/data/ready`)
  para evitar que as duas APIs façam o trabalho duas vezes.
- **mmap read-only** do `refs.bin` em ambas as APIs; o kernel compartilha as páginas.
- **Endpoint `/ready` retorna 503** enquanto o preprocess não terminou.

## Limites de recursos

| Serviço | CPU  | Memória |
|---------|------|---------|
| api1    | 0.45 | 165 MB  |
| api2    | 0.45 | 165 MB  |
| lb      | 0.10 | 20 MB   |
| Total   | 1.00 | 350 MB  |
