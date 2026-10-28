#!/usr/bin/env bash
# Josour server installer: from a fresh Ubuntu/Debian VPS to a running server, in one command.
#
#   curl -fsSL https://raw.githubusercontent.com/mo-shaker/josour/main/deploy/install.sh -o josour-install.sh
#   sudo bash josour-install.sh
#
# It asks four questions (the server's name, an email for the certificate, the administrator's
# email and password), then: checks the DNS record, updates the system, turns on the firewall and
# automatic security updates, installs Docker, fetches the latest Josour release into /opt/josour,
# writes deploy/.env and deploy/.env.relay with fresh secrets, starts the server and the relay,
# creates the administrator and takes a first backup.
#
# Run it again at any time to UPDATE the server to the newest release (or to repair it): an
# existing deploy/.env is kept as it is, a backup is taken first, and nothing is asked.
#
# Every answer can be given in advance, for an unattended run:
#   JOSOUR_DOMAIN=josour.example.com JOSOUR_EMAIL=you@example.com \
#   JOSOUR_ADMIN_EMAIL=you@example.com JOSOUR_ADMIN_NAME="Your Name" JOSOUR_ADMIN_PASSWORD=... \
#   sudo -E bash josour-install.sh
# Other settings: JOSOUR_DIR (default /opt/josour), JOSOUR_REF (a release tag or a branch; default
# the newest release), JOSOUR_RELAY=no (no relay on this machine), JOSOUR_SKIP_DNS_CHECK=1,
# JOSOUR_SKIP_SYSTEM=1 (leave packages, firewall and swap alone).
#
# The manual equivalent of every step, and what each one is for: docs/self-hosting.md.

# Everything is inside main(), so bash has read the whole script before it runs a line of it: an
# update checks out a new version of this very file, which must not change under a running shell.
main() {
set -Eeuo pipefail

readonly REPO_URL="${JOSOUR_REPO:-https://github.com/mo-shaker/josour.git}"
readonly RELEASES_URL="https://github.com/mo-shaker/josour/releases/latest"
readonly DIR="${JOSOUR_DIR:-/opt/josour}"
readonly LOG="/var/log/josour-install.log"
readonly RELAY_PUBLIC_PORT=8443
trap 'die "unexpected error at line $LINENO of the installer"' ERR

if [[ -t 1 ]]; then
  BOLD=$'\e[1m' RED=$'\e[31m' GREEN=$'\e[32m' YELLOW=$'\e[33m' RESET=$'\e[0m'
else
  BOLD='' RED='' GREEN='' YELLOW='' RESET=''
fi
STEP_NO=0
STEP_TOTAL=8

case "${1:-}" in
  -h | --help) sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; return 0 ;;
  "") ;;
  *) die "unknown option $1 (try --help)" ;;
esac

[[ $EUID -eq 0 ]] || die "run it as root: sudo bash $0"
: >>"$LOG" && chmod 600 "$LOG"
echo "=== josour install $(date -u +%FT%TZ)" >>"$LOG"

INTERACTIVE=1
{ : </dev/tty; } 2>/dev/null || INTERACTIVE=0

banner
preflight

local deploy="$DIR/deploy"
local updating=0
[[ -f $deploy/.env ]] && updating=1

# ------------------------------------------------------------------------------------- questions
local domain email admin_email="" admin_name="" admin_password=""
if ((updating)); then
  domain="$(env_get "$deploy/.env" DOMAIN)"
  email="$(env_get "$deploy/.env" ACME_EMAIL)"
  [[ -n $domain ]] || die "$deploy/.env exists but has no DOMAIN. Set it there (or move the file away to start afresh)."
  say "An existing installation was found for ${BOLD}$domain${RESET}: it will be updated to the newest release."
  say "Its settings ($deploy/.env) are kept as they are, and a backup is taken first."
  admin_email="${JOSOUR_ADMIN_EMAIL:-}"
  if [[ -n $admin_email ]]; then
    admin_name="${JOSOUR_ADMIN_NAME:-}"
    admin_password="${JOSOUR_ADMIN_PASSWORD:-}"
  fi
else
  say "A few questions first. Press Enter to accept the value shown in [brackets]."
  echo
  domain="${JOSOUR_DOMAIN:-}"
  while :; do
    ask domain "The server's name (the domain you pointed at this server, e.g. josour.example.com)"
    domain="$(tr '[:upper:]' '[:lower:]' <<<"$domain")"
    domain="${domain#https://}"
    domain="${domain#http://}"
    domain="${domain%%/*}"
    [[ $domain =~ ^([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}$ ]] && break
    warn "\"$domain\" is not a domain name. Type only the name, like josour.example.com"
    domain=""
    ((INTERACTIVE)) || die "JOSOUR_DOMAIN is not a valid domain name"
  done
  email="${JOSOUR_EMAIL:-}"
  while :; do
    ask email "Your email address (Let's Encrypt writes here if the certificate has a problem)"
    valid_email "$email" && break
    warn "\"$email\" does not look like an email address."
    email=""
    ((INTERACTIVE)) || die "JOSOUR_EMAIL is not a valid email address"
  done
  echo
  say "Now the administrator: the account you will use to sign in to the dashboard and the app."
  admin_email="${JOSOUR_ADMIN_EMAIL:-}"
  while :; do
    ask admin_email "Administrator's email" "$email"
    valid_email "$admin_email" && break
    warn "\"$admin_email\" does not look like an email address."
    admin_email=""
    ((INTERACTIVE)) || die "JOSOUR_ADMIN_EMAIL is not a valid email address"
  done
  admin_name="${JOSOUR_ADMIN_NAME:-}"
  ask admin_name "Administrator's name, as people will see it" "${admin_email%%@*}"
  admin_password="${JOSOUR_ADMIN_PASSWORD:-}"
  ask_password admin_password
fi

# ------------------------------------------------------------------------------------------ DNS
step "Checking that $domain points at this server"
if ((updating)) || [[ ${JOSOUR_SKIP_DNS_CHECK:-0} == 1 ]]; then
  say "  skipped"
else
  apt_install_quiet curl ca-certificates dnsutils
  check_dns "$domain"
fi

# ----------------------------------------------------------------------------------- the system
step "Preparing the system (updates, firewall, automatic security updates)"
if [[ ${JOSOUR_SKIP_SYSTEM:-0} == 1 ]]; then
  say "  skipped (JOSOUR_SKIP_SYSTEM=1)"
else
  run "Installing system updates (this can take a few minutes)" apt_upgrade
  run "Installing the firewall, fail2ban and automatic updates" \
    apt_get install -y ufw fail2ban unattended-upgrades ca-certificates curl git openssl dnsutils
  run "Turning on automatic security updates" enable_auto_updates
  run "Configuring the firewall" configure_firewall
  ensure_swap
fi

step "Installing Docker"
if docker compose version >/dev/null 2>&1; then
  say "  already installed: $(docker --version)"
else
  run "Downloading and installing Docker (a few minutes)" install_docker
fi
run "Starting Docker" systemctl enable --now docker

# ------------------------------------------------------------------------------------- the code
step "Fetching Josour"
command -v git >/dev/null || apt_install_quiet git
if ((updating)) && [[ -d $DIR/.git ]]; then
  run "Taking a backup before the update" backup_now "$deploy"
fi
local ref="${JOSOUR_REF:-}"
if [[ -z $ref ]]; then
  ref="$(latest_release_tag || true)"
  [[ -n $ref ]] || { warn "could not find a release; using the main branch"; ref=main; }
fi
fetch_code "$ref"
say "  version: ${BOLD}$(git -C "$DIR" describe --tags --always 2>/dev/null)${RESET}"

# --------------------------------------------------------------------------------- the settings
step "Writing the settings"
local relay_local=0
write_settings "$deploy" "$domain" "$email"
[[ -f $deploy/.env.relay && "$(env_get "$deploy/.env" RELAY_HOST)" == "$domain" ]] && relay_local=1

# ---------------------------------------------------------------------------------- starting up
step "Starting the server (the first start builds it: allow 5 to 10 minutes)"
cd "$deploy"
run "Building and starting the API, the database, the web server and backups" \
  docker compose up -d --build --remove-orphans
if ((relay_local)); then
  run "Building and starting the relay (port $RELAY_PUBLIC_PORT)" \
    docker compose -p josour-relay --env-file .env.relay -f docker-compose.relay.yml up -d --build
fi
run "Waiting for the API to report healthy" wait_healthy api 600
check_https "$domain"
((relay_local)) && check_relay_listening

# ------------------------------------------------------------------------------- administrator
step "The administrator account"
if [[ -n $admin_email && -n $admin_password ]]; then
  create_admin "$admin_email" "$admin_name" "$admin_password"
else
  say "  skipped (an existing installation keeps its accounts)"
fi
admin_password=""

step "A first backup, and tidying up"
run "Writing a backup of the database" backup_now "$deploy"
run "Removing old, unused server images" docker image prune -f

summary "$domain" "$relay_local" "$updating"
}

# ===================================================================================== helpers

say() { printf '%s\n' "$*"; }
warn() { printf '%s! %s%s\n' "$YELLOW" "$*" "$RESET"; }
die() {
  printf '\n%sERROR: %s%s\n' "$RED" "$*" "$RESET" >&2
  [[ -f ${LOG:-} ]] && printf 'The full log is %s\n' "$LOG" >&2
  exit 1
}

banner() {
  cat <<EOF

${BOLD}Josour server installer${RESET}
This installs (or updates) a Josour server on this machine, in $DIR.
The progress is logged to $LOG.
EOF
}

step() {
  STEP_NO=$((STEP_NO + 1))
  printf '\n%s[%d/%d] %s%s\n' "$BOLD" "$STEP_NO" "$STEP_TOTAL" "$*" "$RESET"
}

# run "what it does" command... : the command's own output goes to the log; on failure the tail of
# the log is shown and the installer stops.
run() {
  local what=$1 rc=0
  shift
  printf '  - %s ... ' "$what"
  printf '\n=== %s %s: %s\n' "$(date -u +%T)" "$what" "$*" >>"$LOG"
  "$@" >>"$LOG" 2>&1 || rc=$?
  if ((rc == 0)); then
    printf '%sdone%s\n' "$GREEN" "$RESET"
    return 0
  fi
  printf '%sfailed%s\n' "$RED" "$RESET"
  tail -n 25 "$LOG" | sed 's/^/    | /' >&2
  die "\"$what\" did not work (exit code $rc)."
}

# ask VAR "question" [default]: VAR already set (from the environment) wins.
ask() {
  local __var=$1 __question=$2 __default=${3:-} __answer=""
  [[ -n ${!__var:-} ]] && return 0
  if ((!INTERACTIVE)); then
    [[ -n $__default ]] || die "no terminal to ask \"$__question\"; set it in the environment (see --help)"
    printf -v "$__var" '%s' "$__default"
    return 0
  fi
  while [[ -z $__answer ]]; do
    if [[ -n $__default ]]; then
      printf '%s [%s]: ' "$__question" "$__default" >/dev/tty
    else
      printf '%s: ' "$__question" >/dev/tty
    fi
    IFS= read -r __answer </dev/tty || die "no answer"
    __answer="${__answer#"${__answer%%[![:space:]]*}"}"
    __answer="${__answer%"${__answer##*[![:space:]]}"}"
    __answer=${__answer:-$__default}
  done
  printf -v "$__var" '%s' "$__answer"
}

ask_password() {
  local __var=$1 first second
  if [[ -n ${!__var:-} ]]; then
    local preset=${!__var}
    ((${#preset} >= 8)) || die "JOSOUR_ADMIN_PASSWORD must be at least 8 characters"
    return 0
  fi
  ((INTERACTIVE)) || die "no terminal to ask for the administrator's password; set JOSOUR_ADMIN_PASSWORD"
  say "Choose the administrator's password: at least 8 characters. Nothing appears on the screen as you"
  say "type it - that is normal."
  while :; do
    printf 'Password: ' >/dev/tty
    IFS= read -rs first </dev/tty
    printf '\nThe same password again: ' >/dev/tty
    IFS= read -rs second </dev/tty
    printf '\n' >/dev/tty
    if ((${#first} < 8)); then
      warn "That is shorter than 8 characters. Try again."
    elif [[ $first != "$second" ]]; then
      warn "The two passwords are not the same. Try again."
    else
      break
    fi
  done
  printf -v "$__var" '%s' "$first"
}

valid_email() {
  [[ $1 =~ ^[A-Za-z0-9._%+-]+@([A-Za-z0-9-]+\.)+[A-Za-z]{2,}$ ]]
}

# env_get FILE KEY: the value of KEY=... in a .env file (empty when absent).
env_get() {
  [[ -f $1 ]] || return 0
  sed -n "s/^$2=//p" "$1" | tail -n 1
}

# env_set FILE KEY VALUE: replace KEY's line, or append it; the file stays private.
env_set() {
  local file=$1 key=$2 value=$3 tmp
  tmp="$(mktemp "$file.XXXXXX")"
  awk -v k="$key" -v v="$value" '
    index($0, k "=") == 1 { if (!done) print k "=" v; done = 1; next }
    { print }
    END { if (!done) print k "=" v }' "$file" >"$tmp"
  chmod 600 "$tmp"
  mv "$tmp" "$file"
}

apt_get() {
  DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=600 \
    -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold "$@"
}

apt_upgrade() {
  apt_get update && apt_get -y upgrade
}

apt_install_quiet() {
  local missing=() pkg
  for pkg in "$@"; do
    dpkg -s "$pkg" >/dev/null 2>&1 || missing+=("$pkg")
  done
  ((${#missing[@]})) || return 0
  run "Installing ${missing[*]}" bash -c "$(declare -f apt_get); apt_get update && apt_get install -y ${missing[*]}"
}

preflight() {
  local id="" version="" like=""
  if [[ -r /etc/os-release ]]; then
    # shellcheck disable=SC1091
    id="$(. /etc/os-release && echo "${ID:-}")"
    version="$(. /etc/os-release && echo "${VERSION_ID:-}")"
    like="$(. /etc/os-release && echo "${ID_LIKE:-}")"
  fi
  case "$id" in
    ubuntu | debian) ;;
    *)
      [[ $like == *debian* ]] || die "this installer supports Ubuntu and Debian (found \"${id:-unknown}\"). Ubuntu 24.04 is recommended; docs/self-hosting.md has the manual steps for other systems."
      ;;
  esac
  say "System: $id $version, $(uname -m), $(awk '/MemTotal/ {printf "%.1f GB of memory", $2/1048576}' /proc/meminfo)"

  # On a fresh install, something else holding the web ports means this is not a server of its
  # own: Josour's web server (Caddy) needs 80 and 443.
  if [[ ! -f $DIR/deploy/.env ]] && command -v ss >/dev/null; then
    local busy
    busy="$(ss -H -ltnp 2>/dev/null | awk '$4 ~ /:(80|443|8443)$/' | grep -v docker-proxy || true)"
    if [[ -n $busy ]]; then
      say "$busy" >>"$LOG"
      die "another program already uses port 80, 443 or 8443 on this machine (a web server such as nginx or Apache?). Josour needs a server of its own. Details: $LOG"
    fi
  fi
}

public_ipv4() {
  local url ip
  for url in https://api.ipify.org https://ifconfig.me/ip https://icanhazip.com; do
    ip="$(curl -4 -fsS --max-time 8 "$url" 2>/dev/null | tr -d '[:space:]')" || continue
    [[ $ip =~ ^[0-9]+(\.[0-9]+){3}$ ]] && { echo "$ip"; return 0; }
  done
  return 1
}

resolve_a() {
  local answers=""
  if command -v dig >/dev/null; then
    answers="$(dig +short +time=3 +tries=2 A "$1" @1.1.1.1 2>/dev/null | grep -E '^[0-9.]+$' || true)"
  fi
  [[ -n $answers ]] || answers="$(getent ahostsv4 "$1" 2>/dev/null | awk '{print $1}' | sort -u || true)"
  echo "$answers"
}

check_dns() {
  local domain=$1 ip answers deadline=$((SECONDS + 900)) reply
  ip="$(public_ipv4)" || {
    warn "could not find this server's public address; skipping the DNS check"
    return 0
  }
  say "  this server's public address: ${BOLD}$ip${RESET}"
  while :; do
    answers="$(resolve_a "$domain")"
    if grep -qx "$ip" <<<"$answers"; then
      say "  ${GREEN}$domain points here.${RESET}"
      break
    fi
    if [[ -z $answers ]]; then
      warn "$domain does not point anywhere yet."
    else
      warn "$domain points at $(tr '\n' ' ' <<<"$answers")- not at this server ($ip)."
    fi
    cat <<EOF
  At the company where you bought the domain, open its DNS settings and add a record:
      Type: A      Name: ${domain%%.*} (or @ for the bare domain)      Value: $ip
  A new record usually works within 5 to 30 minutes.
EOF
    if ((INTERACTIVE)); then
      printf "  Press Enter to check again, or type \"skip\" to continue anyway: " >/dev/tty
      IFS= read -r reply </dev/tty || reply=""
      [[ $reply == skip ]] && { warn "continuing without a working DNS record: the certificate will fail until it exists"; break; }
    else
      ((SECONDS < deadline)) || die "$domain still does not point at $ip after 15 minutes"
      sleep 30
    fi
  done
  if command -v dig >/dev/null && [[ -n "$(dig +short AAAA "$domain" @1.1.1.1 2>/dev/null | grep ':' || true)" ]]; then
    warn "$domain also has an AAAA (IPv6) record. Delete it at your DNS provider: with it, people reaching the server over IPv6 all appear to come from one address, which weakens the sign-in protections (docs/self-hosting.md, 2.1)."
  fi
}

# The helpers that run() calls chain their commands with &&: bash ignores `set -e` inside a
# function called from a condition, which is how run() calls them.
enable_auto_updates() {
  printf '%s\n' 'APT::Periodic::Update-Package-Lists "1";' 'APT::Periodic::Unattended-Upgrade "1";' \
    >/etc/apt/apt.conf.d/20auto-upgrades &&
    systemctl enable --now unattended-upgrades &&
    systemctl enable --now fail2ban
}

configure_firewall() {
  local ssh_port
  # The port SSH really listens on: turning the firewall on without it would lock you out.
  ssh_port="$(sshd -T 2>/dev/null | awk '$1 == "port" {print $2; exit}' || true)"
  ssh_port="${ssh_port:-22}"
  echo "ssh port: $ssh_port"
  ufw allow "$ssh_port/tcp" &&
    ufw allow 80/tcp &&
    ufw allow 443/tcp &&
    ufw allow 443/udp &&
    ufw allow "$RELAY_PUBLIC_PORT/tcp" &&
    ufw default deny incoming &&
    ufw default allow outgoing &&
    ufw --force enable
}

# A 1 GB server runs Josour comfortably but can run out of memory while building it.
ensure_swap() {
  local mem_kb
  mem_kb="$(awk '/MemTotal/ {print $2}' /proc/meminfo)"
  if ((mem_kb < 1800000)) && [[ -z "$(swapon --show --noheadings 2>/dev/null)" ]] && [[ ! -e /swapfile ]]; then
    run "Adding 2 GB of swap (this server has little memory)" make_swap
  fi
}

make_swap() {
  { fallocate -l 2G /swapfile || dd if=/dev/zero of=/swapfile bs=1M count=2048; } &&
    chmod 600 /swapfile &&
    mkswap /swapfile &&
    swapon /swapfile &&
    { grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >>/etc/fstab; }
}

install_docker() {
  local script rc=0
  script="$(mktemp)"
  { curl -fsSL https://get.docker.com -o "$script" && sh "$script"; } || rc=$?
  rm -f "$script"
  return "$rc"
}

latest_release_tag() {
  git ls-remote --tags --refs "$REPO_URL" 'v*' 2>>"$LOG" |
    awk -F/ '{print $3}' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sort -V | tail -n 1
}

fetch_code() {
  local ref=$1
  if [[ ! -d $DIR/.git ]]; then
    [[ -e $DIR && -n "$(ls -A "$DIR" 2>/dev/null)" ]] && die "$DIR exists and is not a Josour checkout. Move it away, or set JOSOUR_DIR."
    run "Downloading Josour ($ref)" git clone --quiet "$REPO_URL" "$DIR"
  else
    local changed
    changed="$(git -C "$DIR" status --porcelain --untracked-files=no)"
    [[ -z $changed ]] || die "files of the Josour checkout in $DIR were edited by hand:
$changed
Undo those edits (git -C $DIR stash), or keep them and update by hand (docs/self-hosting.md, 7.1)."
    run "Downloading the newest code" git -C "$DIR" fetch --quiet --tags --prune --force origin
  fi
  if git -C "$DIR" show-ref --verify --quiet "refs/remotes/origin/$ref"; then
    run "Switching to $ref" checkout_branch "$ref"
  else
    run "Switching to $ref" git -C "$DIR" -c advice.detachedHead=false checkout --quiet "$ref"
  fi
}

checkout_branch() {
  { git -C "$DIR" checkout --quiet "$1" 2>/dev/null || git -C "$DIR" checkout --quiet -b "$1" "origin/$1"; } &&
    git -C "$DIR" merge --quiet --ff-only "origin/$1"
}

write_settings() {
  local deploy=$1 domain=$2 email=$3 env="$1/.env" relay_env="$1/.env.relay" key relay_secret
  if [[ ! -f $env ]]; then
    install -m 600 "$deploy/.env.example" "$env"
    say "  created $env"
  fi
  chmod 600 "$env"
  [[ -n "$(env_get "$env" DOMAIN)" ]] || env_set "$env" DOMAIN "$domain"
  [[ -n "$(env_get "$env" ACME_EMAIL)" ]] || env_set "$env" ACME_EMAIL "$email"
  for key in POSTGRES_PASSWORD JWT_SECRET; do
    [[ -n "$(env_get "$env" "$key")" ]] || env_set "$env" "$key" "$(openssl rand -hex 32)"
  done

  # The relay: what makes a session work when neither machine is reachable from the internet
  # (two mobile networks, carrier-grade NAT). On this machine Caddy holds 443, so it takes 8443.
  local relay_host
  relay_host="$(env_get "$env" RELAY_HOST)"
  if [[ -n $relay_host ]]; then
    say "  the relay is already configured ($relay_host): leaving it as it is"
  elif [[ ${JOSOUR_RELAY:-yes} == no ]]; then
    say "  no relay on this machine (JOSOUR_RELAY=no)"
  else
    relay_secret="$(env_get "$env" RELAY_SECRET)"
    [[ -n $relay_secret ]] || relay_secret="$(env_get "$relay_env" RELAY_SECRET)"
    [[ -n $relay_secret ]] || relay_secret="$(openssl rand -hex 32)"
    [[ -f $relay_env ]] || { install -m 600 "$deploy/.env.relay.example" "$relay_env"; say "  created $relay_env"; }
    chmod 600 "$relay_env"
    env_set "$relay_env" RELAY_SECRET "$relay_secret"
    env_set "$relay_env" RELAY_PUBLIC_PORT "$RELAY_PUBLIC_PORT"
    env_set "$env" RELAY_HOST "$domain"
    env_set "$env" RELAY_PORT "$RELAY_PUBLIC_PORT"
    env_set "$env" RELAY_SECRET "$relay_secret"
    say "  the relay will run on this machine, on port $RELAY_PUBLIC_PORT"
  fi
  relay_secret=""
  say "  settings are in $env (keep a copy somewhere safe: it holds the server's passwords)"
}

wait_healthy() {
  local service=$1 deadline=$((SECONDS + $2)) id status
  while ((SECONDS < deadline)); do
    id="$(docker compose ps -q "$service" 2>/dev/null || true)"
    if [[ -n $id ]]; then
      status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$id" 2>/dev/null || true)"
      echo "$service: ${status:-unknown}"
      [[ $status == healthy ]] && return 0
    fi
    sleep 5
  done
  docker compose logs --tail 40 "$service"
  return 1
}

check_https() {
  local domain=$1 deadline=$((SECONDS + 300)) body=""
  printf '  - Waiting for the certificate and https://%s ... ' "$domain"
  while ((SECONDS < deadline)); do
    body="$(curl -fsS --max-time 10 "https://$domain/healthz" 2>>"$LOG" || true)"
    [[ $body == *'"product":"josour"'* ]] && break
    sleep 10
  done
  if [[ $body == *'"product":"josour"'* ]]; then
    printf '%sdone%s\n' "$GREEN" "$RESET"
    return 0
  fi
  printf '%snot yet%s\n' "$YELLOW" "$RESET"
  docker compose logs --tail 30 caddy >>"$LOG" 2>&1 || true
  if curl -fsS --max-time 10 --resolve "$domain:443:127.0.0.1" "https://$domain/healthz" >/dev/null 2>&1; then
    warn "the server works on this machine, but https://$domain does not answer from outside. Open TCP ports 80 and 443 in your hosting provider's firewall (its control panel), if it has one."
  else
    warn "no certificate yet. The usual reasons: the DNS record does not point here yet, or port 80 is closed in your hosting provider's firewall. Fix that, then run this installer again. Details: docker compose -f $DIR/deploy/docker-compose.yml logs caddy"
  fi
}

check_relay_listening() {
  if timeout 3 bash -c "</dev/tcp/127.0.0.1/$RELAY_PUBLIC_PORT" 2>/dev/null; then
    say "  - The relay is listening on port $RELAY_PUBLIC_PORT ... ${GREEN}done${RESET}"
  else
    warn "the relay is not answering on port $RELAY_PUBLIC_PORT yet: docker compose -p josour-relay --env-file .env.relay -f docker-compose.relay.yml logs relay"
  fi
}

create_admin() {
  local admin_email=$1 name=$2 password=$3 out rc=0
  printf '  - Creating the administrator %s ... ' "$admin_email"
  # The password goes in on standard input, never on a command line other processes can read.
  out="$(printf '%s\n' "$password" |
    docker compose exec -T api python manage.py create-admin --email "$admin_email" --display-name "$name" 2>&1)" || rc=$?
  out="$(grep -v -e GetPassWarning -e 'passwd = fallback_getpass' -e 'may be echoed' -e '^Password:' <<<"$out" || true)"
  echo "$out" >>"$LOG"
  if ((rc == 0)); then
    printf '%sdone%s\n' "$GREEN" "$RESET"
  elif [[ $out == *"already exists"* ]]; then
    printf '%salready there%s\n' "$YELLOW" "$RESET"
    say "    An account with this email exists already; its password was not changed."
  else
    printf '%sfailed%s\n' "$RED" "$RESET"
    sed 's/^/    | /' <<<"$out" >&2
    die "the administrator was not created. Create it by hand: cd $DIR/deploy && docker compose exec api python manage.py create-admin --email $admin_email"
  fi
}

backup_now() {
  local deploy=$1
  [[ -f $deploy/docker-compose.yml ]] || return 0
  # Nothing to back up before the first start.
  [[ -n "$(docker compose --project-directory "$deploy" ps -q backup 2>/dev/null || true)" ]] || return 0
  docker compose --project-directory "$deploy" exec -T backup /usr/local/bin/backup.sh
}

summary() {
  local domain=$1 relay_local=$2 updating=$3 version
  version="$(curl -fsS --max-time 10 "https://$domain/healthz" 2>/dev/null | sed -n 's/.*"version":"\([^"]*\)".*/\1/p' || true)"
  cat <<EOF

${GREEN}${BOLD}Josour is $( ((updating)) && echo updated || echo installed ).${RESET} ${version:+(server version $version)}

  ${BOLD}The dashboard${RESET}               https://$domain/admin
                              sign in with the administrator's email and password, then
                              turn on two-factor authentication under "My account".
  ${BOLD}Server address for the app${RESET}  https://$domain
  ${BOLD}Download the app${RESET}            $RELEASES_URL
                              (Windows: Josour-win-x64.exe - macOS: the .dmg for your Mac)

  ${BOLD}Settings and passwords${RESET}      $DIR/deploy/.env$( ((relay_local)) && echo " and .env.relay")
                              keep a copy of these files somewhere other than this server.
  ${BOLD}Backups${RESET}                     $DIR/deploy/backups (one a day, kept 14 days)
  ${BOLD}To update later${RESET}             sudo bash $DIR/deploy/install.sh

If your hosting provider has a firewall in its control panel, open these ports there:
  TCP 22, 80, 443$( ((relay_local)) && echo ", $RELAY_PUBLIC_PORT") and UDP 443.
EOF
}

main "$@"
