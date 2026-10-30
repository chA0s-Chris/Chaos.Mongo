# The Mongo image with iproute2 added, so the load test can inject network latency with tc netem.
# MongoTopology always supplies MONGO_IMAGE; there is no supported default.
ARG MONGO_IMAGE
FROM ${MONGO_IMAGE}
RUN apt-get update \
    && apt-get install -y --no-install-recommends iproute2 \
    && rm -rf /var/lib/apt/lists/*
