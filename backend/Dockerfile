FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /app

COPY pyproject.toml LICENSE NOTICE ./
COPY app ./app
RUN pip install . && rm -rf build *.egg-info

COPY alembic.ini manage.py ./
COPY alembic ./alembic

RUN useradd --system --uid 10001 --create-home routebridge \
    && chown -R routebridge:routebridge /app
USER routebridge

EXPOSE 8000

# Single worker by design (in-memory WebSocket registry). Caddy terminates TLS in front of
# this container on a private network, so forwarded headers are trusted from any peer.
CMD ["sh", "-c", "alembic upgrade head && exec uvicorn app.main:app --host 0.0.0.0 --port 8000 --workers 1 --proxy-headers --forwarded-allow-ips='*'"]
