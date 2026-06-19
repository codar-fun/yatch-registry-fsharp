# yatch-registry-fsharp

A lightweight OCI Distribution (Docker Registry v2) server in **F# / ASP.NET Core**,
backed by S3-compatible object storage (AWS S3, Cloudflare R2, **Aliyun OSS**) with
metadata in **SQLite** or **Postgres**.

This is a from-scratch F# reimplementation of the Rust `yatch`, wire- and
storage-compatible with it: identical OSS key layout (`blobs/<digest>`,
`manifests/<repo>/<digest>`, `uploads/<uuid>`) and identical Postgres schema
(`tags`, `manifests`, `uploads`), so it can run against an existing yatch
Postgres database and OSS bucket as a drop-in replacement.

## Features

- **OCI Distribution v2**: manifests, blobs, chunked + monolithic blob uploads,
  cross-repo blob mount, tag listing, catalog.
- **Shared metadata**: SQLite (`DB_PATH`) or Postgres (`DATABASE_URL`). With one
  Postgres + one bucket, multiple instances share the same images — e.g. push to
  a fast overseas mirror, pull from an in-region registry.
- **OSS-compatible storage**: virtual-hosted addressing, checksum-when-required,
  internal/public endpoints, presigned blob GET redirects (private buckets).
- **Concurrent, memory-bounded, self-recovering uploads**: blobs stream to S3
  multipart with bounded parallelism (memory ≈ 4 × 8 MB), per-part retry, and
  socket + attempt timeouts so a stalled connection aborts and retries instead
  of hanging.
- **Resumable uploads**: the S3 upload id + completed parts are persisted, so an
  interrupted `docker push` resumes from its last offset.
- **Realtime progress API**: `GET /_progress` returns bytes, total, percent, and
  average + recent transfer speed for every in-flight upload.
- **Auth**: optional static token via `Authorization: Bearer <token>` or HTTP
  Basic (password == token). When `AUTH_TOKEN` is set, the whole registry API
  (`/v2/*`) and `/_progress` require auth — so `docker login` actually validates
  credentials (returns `401 WWW-Authenticate: Basic` until correct).
- **Operational**: unauthenticated `GET /healthz` for liveness/health checks
  (use this, not `/v2/`, for Nomad/Traefik checks when auth is on); per-request
  and startup logging (gated by `LOG_LEVEL`); all unhandled errors return a clean
  OCI `INTERNAL_ERROR` JSON instead of leaking a stack trace.

## Configuration (environment variables)

| Var | Default | Notes |
|---|---|---|
| `HOST` / `PORT` | `0.0.0.0` / `5000` | listen address |
| `S3_BUCKET` | — (required) | object storage bucket |
| `S3_REGION` | `us-east-1` | e.g. `oss-cn-hangzhou` |
| `S3_ENDPOINT` | — | custom endpoint (OSS/R2/MinIO) |
| `S3_PUBLIC_URL` | — | if set, blob GET redirects here; else presigned URLs |
| `S3_FORCE_PATH_STYLE` | auto | `false` for OSS (virtual-hosted) |
| `PRESIGN_TTL_SECS` | `3600` | presigned URL lifetime |
| `DB_PATH` | `./yatch.db` | SQLite path (when no `DATABASE_URL`) |
| `DATABASE_URL` | — | `postgres://user:pass@host:port/db` → shared Postgres |
| `AUTH_TOKEN` | — | if set, auth required |
| `AUTH_USER` | — | optional Basic-auth username |
| `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` | — | object storage creds |
| `AWS_REQUEST_CHECKSUM_CALCULATION` | `when_required` (in Docker) | OSS compat |

## Run

```sh
dotnet run --project src                 # local (SQLite)
docker build -t yatch-registry:local .   # container image
```

## Progress API

```sh
curl -s localhost:5000/_progress | jq
# { "uploads": [ { "uuid": "...", "repo": "sails", "bytes": 12582912,
#   "total": 41943040, "percent": 30.0, "avg_bps": 4194304, "recent_bps": 6291456 } ] }
```
