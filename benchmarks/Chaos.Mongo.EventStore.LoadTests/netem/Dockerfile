# mongo:8 with iproute2, so the load test can inject network latency with tc netem.
FROM mongo:8
RUN apt-get update \
    && apt-get install -y --no-install-recommends iproute2 \
    && rm -rf /var/lib/apt/lists/*
