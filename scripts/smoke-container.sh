#!/usr/bin/env bash
set -euo pipefail

candidate_image=${1:?Usage: smoke-container.sh IMAGE}
smoke_directory=$(mktemp -d /tmp/ProtonBridgeBridge-smoke.XXXXXX)
smoke_image="protonbridgebridge-smoke:$$-$RANDOM"
container_id=""
cleanup() {
    if [[ -n "$container_id" ]]; then
        docker rm -f "$container_id" >/dev/null 2>&1 || true
    fi
    docker image rm "$smoke_image" >/dev/null 2>&1 || true
    rm -rf -- "$smoke_directory"
}
trap cleanup EXIT

# These certificates are disposable fixture material; the smoke container has no network or mailbox credentials.
openssl req -x509 -newkey rsa:2048 -sha256 -days 1 -nodes \
    -keyout "$smoke_directory/proxy.key" -out "$smoke_directory/proxy.pem" \
    -subj /CN=localhost -addext subjectAltName=DNS:localhost >/dev/null 2>&1
chmod 644 "$smoke_directory/proxy.key"
cat > "$smoke_directory/config.json" <<'JSON'
{"Certificate":"/certs/proxy.pem","PrivateKey":"/certs/proxy.key","BridgeTrust":"/certs/proxy.pem","BridgePort":59999,"StateDirectory":"/state"}
JSON
cat > "$smoke_directory/Dockerfile" <<EOF_DOCKER
FROM $candidate_image
COPY proxy.pem proxy.key /certs/
COPY config.json /config/config.json
EOF_DOCKER
docker build -q -t "$smoke_image" "$smoke_directory" >/dev/null
container_id=$(docker run -d --network none --read-only --cap-drop ALL --security-opt no-new-privileges:true \
    --tmpfs /state:uid=1654,gid=1654,mode=0700 --tmpfs /tmp:mode=1777 "$smoke_image")

probe() {
    docker exec "$container_id" bash -c \
        'exec 3<>/dev/tcp/127.0.0.1/8080; printf "GET %s HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" "$1" >&3; cat <&3' \
        bash "$1"
}
for attempt in {1..50}; do
    live_response=$(probe /health/live 2>/dev/null || true)
    if [[ "$live_response" == *"200 OK"* && "$live_response" == *Healthy* ]]; then
        break
    fi
    sleep 0.1
done
[[ "$live_response" == *"200 OK"* && "$live_response" == *Healthy* ]]
ready_response=$(probe /health/ready)
[[ "$ready_response" == *"503 Service Unavailable"* && "$ready_response" == *Unhealthy* ]]
docker exec "$container_id" dotnet ProtonBridgeBridge.dll --check-config /config/config.json >/dev/null
if docker exec "$container_id" dotnet ProtonBridgeBridge.dll --healthcheck /config/config.json; then
    echo "Readiness unexpectedly passed without Bridge." >&2
    exit 1
else
    [[ $? -eq 1 ]]
fi
docker stop --time 10 "$container_id" >/dev/null
[[ $(docker wait "$container_id") -eq 0 ]]
echo "Isolated container smoke passed: non-root/read-only startup, liveness, unavailable-Bridge readiness and SIGTERM shutdown."
