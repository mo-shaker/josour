FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /app

COPY pyproject.toml ./
COPY relay ./relay
RUN pip install . && rm -rf build *.egg-info

RUN useradd --system --uid 10002 --create-home josour \
    && chown -R josour:josour /app
USER josour

# Inside the container the relay listens on 8443, because an unprivileged user cannot bind a port
# below 1024; the compose file publishes it as 443 on the host. 443 on the outside is the point:
# it is the one outbound port open on essentially every network, which is what makes the product
# work without router configuration (ADR-0009).
#
# Nothing terminates TLS in front of this. The bytes are already end-to-end encrypted between the
# two peers, so a reverse proxy would only add a hop that cannot read them anyway.
ENV RELAY_PORT=8443
EXPOSE 8443

CMD ["python", "-m", "relay"]
