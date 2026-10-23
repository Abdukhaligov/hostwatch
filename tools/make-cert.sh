#!/bin/sh
# Generates a self-signed TLS certificate + key for the hostwatch server.
#
#   ./make-cert.sh <hostname-or-ip> [more-names-or-ips ...]
#   ./make-cert.sh hostwatch 192.168.31.6
#
# Writes hostwatch.crt (public, shipped to clients) and hostwatch.key (private, keep on the server)
# into the current directory. List every name/IP the agents will use to reach the server - each one
# must be in the certificate, or the agent's https check fails. Point the server at the pair with
# HOSTWATCH_TLS_CERT / HOSTWATCH_TLS_KEY (see run.sh / run.cmd / docker-compose.yml). The agent trusts
# it automatically: hostwatch-setup.exe imports hostwatch.crt into each PC's Trusted Root. Valid 10 years.
set -e

if [ $# -lt 1 ]; then
    echo "usage: $0 <hostname-or-ip> [more-names-or-ips ...]"
    exit 1
fi

dns_count=0
ip_count=0
alt_names=""
for host in "$@"; do
    case "$host" in
        *[!0-9.]*)   # contains something other than digits and dots -> a DNS name
            dns_count=$((dns_count + 1))
            alt_names="${alt_names}DNS.${dns_count} = ${host}
" ;;
        *)           # only digits and dots -> an IPv4 address
            ip_count=$((ip_count + 1))
            alt_names="${alt_names}IP.${ip_count} = ${host}
" ;;
    esac
done

config_file=$(mktemp)
cat > "$config_file" <<EOF
[req]
distinguished_name = dn
x509_extensions = v3
prompt = no
[dn]
CN = $1
[v3]
subjectAltName = @alt
basicConstraints = critical, CA:TRUE
[alt]
${alt_names}
EOF

openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
    -keyout hostwatch.key -out hostwatch.crt -config "$config_file"
rm -f "$config_file"

echo "Wrote hostwatch.crt and hostwatch.key for: $*"
