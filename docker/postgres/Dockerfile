# ============================================================
# PostgreSQL 16 (Alpine/musl) + pgvector — Fase 8.1 (DA8-1)
#
# Misma base que la imagen anterior (postgres:16-alpine): solo añade los binarios de la extensión pgvector,
# compilada desde una etiqueta fija. No cambia la versión mayor ni la biblioteca C, así que el directorio de
# datos existente se monta sin cambios. La extensión se crea con la migración Fase81IndiceSemantico, nunca con
# scripts de inicio. Procedimiento de cambio de imagen: docs/FASE_8_CONTRATO.md §3.1.1.
# ============================================================
FROM postgres:16-alpine

ARG PGVECTOR_VERSION=0.8.7

# OPTFLAGS="" evita -march=native (binario portable entre CPUs); with_llvm=no evita depender de clang/llvm
# para el bitcode JIT, que pgvector no necesita.
RUN set -eux; \
    apk add --no-cache --virtual .pgvector-build-deps git build-base; \
    git clone --branch "v${PGVECTOR_VERSION}" --depth 1 https://github.com/pgvector/pgvector.git /tmp/pgvector; \
    cd /tmp/pgvector; \
    make OPTFLAGS="" with_llvm=no; \
    make install with_llvm=no; \
    cd /; \
    rm -rf /tmp/pgvector; \
    apk del .pgvector-build-deps

LABEL org.opencontainers.image.title="asistente-juridico-postgres" \
      org.opencontainers.image.description="PostgreSQL 16 Alpine con pgvector" \
      pgvector.version="${PGVECTOR_VERSION}"
