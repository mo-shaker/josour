#!/usr/bin/env bash
#
# Spike S1 for ADR-0015: can an ordinary, unsigned app point the macOS system proxy at 127.0.0.1 and put it back
# afterwards, and does macOS ask for a password to let it?
#
# The guest app would do this at the start of every session (docs/decisions/0015-system-proxy-instead-of-work-browser.md).
# The macOS half hangs on one question: does `networksetup` change the proxy silently for a normal user, ask for an
# administrator's password, or refuse? If it ever asks, the app needs a small root-installed helper instead (the app
# is unsigned, so SMJobBless is not available).
#
# Per enabled network service (Wi-Fi, Ethernet, ...; the names may contain spaces) it:
#   1. saves the web proxy, secure web proxy, bypass domains, auto-proxy (PAC) state and auto-discovery state to a
#      snapshot file, BEFORE changing anything;
#   2. points the web and secure web proxy at 127.0.0.1:<port>, sets a bypass list, turns PAC and auto-discovery off;
#   3. reads everything back, and times how long the effective configuration (scutil --proxy) takes to follow;
#   4. waits (Enter, or --hold seconds) while you look at the browsers;
#   5. puts back exactly what it saved, field by field, reads it back again and says whether it matches.
# A trap restores on Ctrl-C, on a closed terminal and on any error too. Every networksetup call is timed and its
# output kept, so the summary says per call whether it ran silently, was slow (a dialog was on screen) or failed.
#
# While the proxy is applied, a dummy listener (dummy-listener.pl) answers on 127.0.0.1:<port>. It forwards NOTHING:
# it writes down each request and answers 502, except for http://check.josour/ (a name that does not exist, so it
# only loads through the proxy) and its own test pages on http://127.0.0.1:<port>/. Apps that honour the system proxy
# therefore have no internet for the minutes the proxy is applied, and anything that still loads is a leak (S3).
#
#   scripts/spikes/system-proxy-macos.sh --dry-run          # read and print the current settings, change nothing
#   scripts/spikes/system-proxy-macos.sh                    # apply, wait for Enter, restore
#   scripts/spikes/system-proxy-macos.sh --hold 400         # stay applied 400 s, longer than a cached authorisation
#   scripts/spikes/system-proxy-macos.sh --restore FILE     # put back the snapshot of a run that was killed
#
# Options: --port N (default 18923), --bypass HOST (repeatable: add the Josour server and relay), --hold SECONDS,
# --linger SECONDS (keep the listener up after the restore, default 20), --out DIR.
# Results go to ~/josour-spike-results/s1-macos-<timestamp>/; summary.txt is the file to send back.
# The steps and the results table: docs/spike-runbook.md, "The system proxy spikes".
#
# Written for the bash 3.2 macOS ships (no associative arrays). No `set -e`: the point is to run commands that may
# fail and write down how they failed.

PORT=18923
HOLD=0
LINGER=20
OUT=""
DRY_RUN=0
RESTORE_FILE=""
EXTRA_BYPASS=()
PROMPT_MS=1500   # a networksetup call this slow was, in practice, waiting on a dialog
NL=$'\n'
TAB=$'\t'

HERE="$(cd "$(dirname "$0")" && pwd)"

die() { echo "error: $*" >&2; exit 1; }
usage() { awk 'NR == 1 { next } /^#/ { sub(/^# ?/, ""); print; next } { exit }' "$0"; }
need_value() { [ "$2" -ge 2 ] || die "$1 needs a value"; }
is_number() { case "$1" in ''|*[!0-9]*) return 1 ;; esac; }

while [ $# -gt 0 ]; do
    case "$1" in
        --port) need_value "$1" $#; PORT="$2"; shift 2 ;;
        --bypass) need_value "$1" $#; EXTRA_BYPASS+=("$2"); shift 2 ;;
        --hold) need_value "$1" $#; HOLD="$2"; shift 2 ;;
        --linger) need_value "$1" $#; LINGER="$2"; shift 2 ;;
        --out) need_value "$1" $#; OUT="$2"; shift 2 ;;
        --restore) need_value "$1" $#; RESTORE_FILE="$2"; shift 2 ;;
        --dry-run) DRY_RUN=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option: $1 (see --help)" ;;
    esac
done

is_number "$PORT" && [ "$PORT" -ge 1024 ] && [ "$PORT" -le 65535 ] || die "--port must be between 1024 and 65535"
is_number "$HOLD" || die "--hold takes a number of seconds"
is_number "$LINGER" || die "--linger takes a number of seconds"

[ "$(uname -s)" = Darwin ] || die "this is the macOS spike; on Windows run scripts/spikes/system-proxy-windows.ps1"
command -v networksetup >/dev/null 2>&1 || die "networksetup not found"
command -v perl >/dev/null 2>&1 || die "perl not found (macOS ships it; the timer and the dummy listener use it)"
[ "$(id -u)" -ne 0 ] || die "do not run this with sudo: the question is what an app running as you is allowed to do"

INTERACTIVE=0
[ -t 0 ] && [ -t 1 ] && INTERACTIVE=1
if id -Gn | tr ' ' '\n' | grep -qx admin; then ACCOUNT=admin; else ACCOUNT=standard; fi
MACOS="$(sw_vers -productVersion 2>/dev/null) ($(sw_vers -buildVersion 2>/dev/null)), $(uname -m)"

BYPASS_LIST=('*.local' '169.254/16' '127.0.0.1' 'localhost' '::1' '10.0.0.0/8' '172.16.0.0/12' '192.168.0.0/16')
BYPASS_LIST+=("${EXTRA_BYPASS[@]}")

SVC=() DISABLED=() SNAP=() ACTIVE=() SKIPPED=()
APPLIED=0 RESTORED=0 FINISHED=0 RESTORE_OK=""
APPLY_START_MS=0 APPLY_DONE_MS=0 RESTORE_DONE_MS=0
APPLY_READBACK="" APPLY_DIFF="" RESTORE_READBACK="" RESTORE_DIFF=""
SCUTIL_APPLY="" SCUTIL_RESTORE="" DIALOGS_APPLY="" DIALOGS_RESTORE=""
LISTENER_PID="" LISTENER_LOG="" CMDLOG="" ANNOUNCED="|"

now_ms() { perl -MTime::HiRes=time -e 'printf("%d\n", time() * 1000)'; }

# --- reading the settings ----------------------------------------------------------------------------------------
#
# A service's settings are kept as TSV text, one "key<TAB>value" per line in a fixed order, so that "is it exactly
# as before?" is a string comparison and the snapshot file is the same text.

kv() { printf '%s\n' "$2" | sed -n "s/^$1: \{0,1\}//p" | head -n 1; }   # kv KEY OUTPUT -> the value of "KEY: value"
tsv_get() { printf '%s\n' "$1" | awk -F'\t' -v k="$2" '$1 == k { sub(/^[^\t]*\t/, ""); print; exit }'; }
tsv_all() { printf '%s\n' "$1" | awk -F'\t' -v k="$2" '$1 == k { sub(/^[^\t]*\t/, ""); print }'; }
differs() { [ -z "$3" ] || [ "$(tsv_get "$2" "$1")" != "$(tsv_get "$3" "$1")" ]; }   # differs FIELD SAVED CURRENT
onoff() { case "$1" in Yes|On|yes|on|1) echo on ;; *) echo off ;; esac; }

capture_tsv() {   # capture_tsv SERVICE -> TSV on stdout; fails if networksetup could not read the service
    local svc="$1" web sec byp pac wpad d
    web=$(networksetup -getwebproxy "$svc" 2>&1) || return 1
    sec=$(networksetup -getsecurewebproxy "$svc" 2>&1) || return 1
    byp=$(networksetup -getproxybypassdomains "$svc" 2>&1) || return 1
    pac=$(networksetup -getautoproxyurl "$svc" 2>&1) || return 1
    wpad=$(networksetup -getproxyautodiscovery "$svc" 2>&1) || return 1
    case "$web$NL$sec$NL$byp$NL$pac$NL$wpad" in *"** Error"*|*"Error:"*) return 1 ;; esac
    printf 'svc\t%s\n' "$svc"
    printf 'web_enabled\t%s\n' "$(kv Enabled "$web")"
    printf 'web_server\t%s\n' "$(kv Server "$web")"
    printf 'web_port\t%s\n' "$(kv Port "$web")"
    printf 'web_auth\t%s\n' "$(kv 'Authenticated Proxy Enabled' "$web")"
    printf 'secure_enabled\t%s\n' "$(kv Enabled "$sec")"
    printf 'secure_server\t%s\n' "$(kv Server "$sec")"
    printf 'secure_port\t%s\n' "$(kv Port "$sec")"
    printf 'secure_auth\t%s\n' "$(kv 'Authenticated Proxy Enabled' "$sec")"
    printf 'pac_enabled\t%s\n' "$(kv Enabled "$pac")"
    printf 'pac_url\t%s\n' "$(kv URL "$pac")"
    printf 'wpad\t%s\n' "$(kv 'Auto Proxy Discovery' "$wpad")"
    case "$byp" in
        "There aren't any"*) ;;
        *) while IFS= read -r d; do [ -n "$d" ] && printf 'bypass\t%s\n' "$d"; done <<EOF
$byp
EOF
        ;;
    esac
}

expected_applied() {   # expected_applied INDEX -> the TSV the service should read back as while applied
    local d
    printf 'svc\t%s\n' "${SVC[$1]}"
    printf 'web_enabled\tYes\nweb_server\t127.0.0.1\nweb_port\t%s\nweb_auth\t0\n' "$PORT"
    printf 'secure_enabled\tYes\nsecure_server\t127.0.0.1\nsecure_port\t%s\nsecure_auth\t0\n' "$PORT"
    printf 'pac_enabled\tNo\npac_url\t%s\nwpad\tOff\n' "$(tsv_get "${SNAP[$1]}" pac_url)"
    for d in "${BYPASS_LIST[@]}"; do printf 'bypass\t%s\n' "$d"; done
}

describe_tsv() {
    local t="$1"
    echo "   $(tsv_get "$t" svc)"
    echo "     web proxy        : $(tsv_get "$t" web_enabled), server '$(tsv_get "$t" web_server)' port $(tsv_get "$t" web_port)"
    echo "     secure web proxy : $(tsv_get "$t" secure_enabled), server '$(tsv_get "$t" secure_server)' port $(tsv_get "$t" secure_port)"
    echo "     bypass domains   : $(tsv_all "$t" bypass | paste -s -d, - | sed 's/,/, /g')"
    echo "     auto proxy (PAC) : $(tsv_get "$t" pac_enabled), url $(tsv_get "$t" pac_url)"
    echo "     auto discovery   : $(tsv_get "$t" wpad)"
}

read_services() {
    local raw line
    raw=$(networksetup -listallnetworkservices 2>&1) || die "networksetup -listallnetworkservices failed: $raw"
    case "$raw" in *"** Error"*) die "networksetup -listallnetworkservices: $raw" ;; esac
    while IFS= read -r line; do
        case "$line" in
            ''|"An asterisk"*) ;;
            '*'*) DISABLED+=("${line#\*}") ;;
            *) SVC+=("$line") ;;
        esac
    done <<EOF
$raw
EOF
    [ ${#SVC[@]} -gt 0 ] || die "no enabled network service"
}

snapshot_all() {   # dies before anything has changed if one service cannot be read
    local i
    for i in "${!SVC[@]}"; do
        SNAP[$i]=$(capture_tsv "${SVC[$i]}") || die "could not read the proxy settings of \"${SVC[$i]}\"; nothing was changed"
        if [ "$(tsv_get "${SNAP[$i]}" web_auth)" = 1 ] || [ "$(tsv_get "${SNAP[$i]}" secure_auth)" = 1 ]; then
            SKIPPED+=("${SVC[$i]} (it uses an authenticated proxy, whose password this could not put back)")
        else
            ACTIVE+=("$i")
        fi
    done
    [ ${#ACTIVE[@]} -gt 0 ] || die "every enabled service uses an authenticated proxy; run this on another machine"
}

write_snapshot() {
    local i
    {
        echo "# Josour spike S1 snapshot, $(date '+%Y-%m-%d %H:%M:%S'), port $PORT."
        echo "# If a run was killed before it could restore, put these settings back with:"
        echo "#   $HERE/system-proxy-macos.sh --restore '$SNAPSHOT'"
        for i in "${ACTIVE[@]}"; do printf '%s\n' "${SNAP[$i]}"; done
    } > "$SNAPSHOT"
}

load_snapshot() {
    local n=-1 key val
    while IFS="$TAB" read -r key val; do
        case "$key" in ''|'#'*) continue ;; esac
        if [ "$key" = svc ]; then
            n=$((n + 1)); SVC[$n]="$val"; SNAP[$n]="svc$TAB$val"; ACTIVE+=("$n")
        elif [ $n -ge 0 ]; then
            SNAP[$n]="${SNAP[$n]}$NL$key$TAB$val"
        fi
    done < "$1"
}

# --- changing them -----------------------------------------------------------------------------------------------

run() {   # run PHASE SERVICE networksetup-arguments...: times one call and writes it down
    local phase="$1" svc="$2" t0 t1 out rc ms verdict
    shift 2
    t0=$(now_ms)
    out=$(networksetup "$@" 2>&1); rc=$?
    t1=$(now_ms); ms=$((t1 - t0))
    out=$(printf '%s' "$out" | tr '\t\n' '  ')
    if [ $rc -ne 0 ]; then verdict=failed
    else
        case "$out" in
            *rror*|*denied*|*uthori*|*rivilege*|*equires*) verdict=failed ;;
            *) if [ $ms -ge $PROMPT_MS ]; then verdict=slow; else verdict=silent; fi ;;
        esac
    fi
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$phase" "$svc" "$1" "$rc" "$ms" "$verdict" "$out" >> "$CMDLOG"
    printf '    %-7s %-26s %6s ms  rc=%s  %s\n' "$verdict" "$1" "$ms" "$rc" "$out"
}

apply_service() {
    local svc="${SVC[$1]}"
    echo "  [$svc]"
    run apply "$svc" -setwebproxy "$svc" 127.0.0.1 "$PORT"
    run apply "$svc" -setwebproxystate "$svc" on
    run apply "$svc" -setsecurewebproxy "$svc" 127.0.0.1 "$PORT"
    run apply "$svc" -setsecurewebproxystate "$svc" on
    run apply "$svc" -setproxybypassdomains "$svc" "${BYPASS_LIST[@]}"
    run apply "$svc" -setautoproxystate "$svc" off
    run apply "$svc" -setproxyautodiscovery "$svc" off
}

restore_proxy_group() {   # restore_proxy_group SERVICE web|secure SAVED CURRENT SET-COMMAND STATE-COMMAND
    local svc="$1" p="$2" saved="$3" cur="$4" set="$5" state="$6" server port enabled
    server=$(tsv_get "$saved" "${p}_server")
    port=$(tsv_get "$saved" "${p}_port")
    enabled=$(onoff "$(tsv_get "$saved" "${p}_enabled")")
    if differs "${p}_server" "$saved" "$cur" || differs "${p}_port" "$saved" "$cur"; then
        # An empty server means none was set before; whether networksetup accepts "" to clear it is itself a finding.
        run restore "$svc" "$set" "$svc" "$server" "${port:-0}"
        # -setwebproxy turns the proxy on, so the state always has to follow it.
        run restore "$svc" "$state" "$svc" "$enabled"
    elif differs "${p}_enabled" "$saved" "$cur"; then
        run restore "$svc" "$state" "$svc" "$enabled"
    fi
}

restore_service() {   # restore_service INDEX: puts back only the fields that differ from the snapshot
    local svc="${SVC[$1]}" saved="${SNAP[$1]}" cur d
    local -a list
    cur=$(capture_tsv "$svc") || cur=""
    if [ -n "$cur" ] && [ "$cur" = "$saved" ]; then
        echo "  [$svc] nothing to put back"
        return 0
    fi
    echo "  [$svc]"
    restore_proxy_group "$svc" web "$saved" "$cur" -setwebproxy -setwebproxystate
    restore_proxy_group "$svc" secure "$saved" "$cur" -setsecurewebproxy -setsecurewebproxystate
    if [ -z "$cur" ] || [ "$(tsv_all "$saved" bypass)" != "$(tsv_all "$cur" bypass)" ]; then
        list=()
        while IFS= read -r d; do [ -n "$d" ] && list+=("$d"); done <<EOF
$(tsv_all "$saved" bypass)
EOF
        [ ${#list[@]} -gt 0 ] || list=(Empty)
        run restore "$svc" -setproxybypassdomains "$svc" "${list[@]}"
    fi
    if differs pac_enabled "$saved" "$cur"; then
        run restore "$svc" -setautoproxystate "$svc" "$(onoff "$(tsv_get "$saved" pac_enabled)")"
    fi
    if differs wpad "$saved" "$cur"; then
        run restore "$svc" -setproxyautodiscovery "$svc" "$(onoff "$(tsv_get "$saved" wpad)")"
    fi
}

restore_all() {
    local i
    for i in "${ACTIVE[@]}"; do restore_service "$i"; done
}

verify_applied() {
    local i exp cur d ok=1
    for i in "${ACTIVE[@]}"; do
        exp=$(expected_applied "$i")
        cur=$(capture_tsv "${SVC[$i]}")
        { echo "== ${SVC[$i]}"; printf '%s\n' "$cur"; } >> "$OUT/readback-applied.txt"
        if [ "$exp" = "$cur" ]; then
            echo "  [${SVC[$i]}] reads back as set"
        elif ! awk -F'\t' -v s="${SVC[$i]}" '$1 == "apply" && $2 == s { f = 1 } END { exit !f }' "$CMDLOG"; then
            echo "  [${SVC[$i]}] not applied (the apply stopped before it)"
        else
            ok=0
            d=$(diff <(printf '%s\n' "$exp") <(printf '%s\n' "$cur") | sed -n 's/^</      set:/p; s/^>/      now:/p')
            echo "  [${SVC[$i]}] reads back DIFFERENT from what was set:"
            printf '%s\n' "$d"
            APPLY_DIFF="$APPLY_DIFF   [${SVC[$i]}]$NL$d$NL"
        fi
    done
    if [ $ok = 1 ]; then APPLY_READBACK="as set"; else APPLY_READBACK="DIFFERENT from what was set (below)"; fi
}

verify_restored() {
    local i cur d ok=1
    RESTORE_DIFF=""
    for i in "${ACTIVE[@]}"; do
        cur=$(capture_tsv "${SVC[$i]}")
        { echo "== ${SVC[$i]}"; printf '%s\n' "$cur"; } >> "$OUT/readback-restored.txt"
        if [ "$cur" = "${SNAP[$i]}" ]; then
            echo "  [${SVC[$i]}] exactly as before"
        else
            ok=0
            d=$(diff <(printf '%s\n' "${SNAP[$i]}") <(printf '%s\n' "$cur") | sed -n 's/^</      before:/p; s/^>/      now:   /p')
            echo "  [${SVC[$i]}] NOT as before:"
            printf '%s\n' "$d"
            RESTORE_DIFF="$RESTORE_DIFF   [${SVC[$i]}]$NL$d$NL"
        fi
    done
    if [ $ok = 1 ]; then
        RESTORE_OK=1; RESTORE_READBACK="exactly as before"
    else
        RESTORE_OK=0
        RESTORE_READBACK="NOT as before. Fix it in System Settings > Network > (service) > Details > Proxies, or rerun with --restore '$SNAPSHOT'"
    fi
}

scutil_ours() {   # does the effective configuration (the primary service) point at our listener?
    local p
    p="$(scutil --proxy 2>/dev/null)$NL"
    case "$p" in *"HTTPEnable : 1$NL"*) ;; *) return 1 ;; esac
    case "$p" in *"HTTPProxy : 127.0.0.1$NL"*) ;; *) return 1 ;; esac
    case "$p" in *"HTTPPort : $PORT$NL"*) return 0 ;; esac
    return 1
}

wait_scutil() {   # wait_scutil ours|not_ours -> how long scutil --proxy took to get there
    local want="$1" t0 now
    t0=$(now_ms)
    while :; do
        if scutil_ours; then [ "$want" = ours ] && break; else [ "$want" = not_ours ] && break; fi
        now=$(now_ms)
        [ $((now - t0)) -lt 10000 ] || { echo "not within 10 s"; return; }
        sleep 0.1
    done
    now=$(now_ms)
    echo "$((now - t0)) ms"
}

# --- the listener and what reached it ----------------------------------------------------------------------------

start_listener() {
    local tries=0
    if nc -z 127.0.0.1 "$PORT" >/dev/null 2>&1; then die "something already listens on 127.0.0.1:$PORT; pick another --port"; fi
    : > "$LISTENER_LOG"
    perl "$HERE/dummy-listener.pl" "$PORT" "$LISTENER_LOG" "$$" "$HERE/pages" 2>>"$OUT/listener.err" &
    LISTENER_PID=$!
    until nc -z 127.0.0.1 "$PORT" >/dev/null 2>&1; do
        kill -0 "$LISTENER_PID" 2>/dev/null || die "the dummy listener exited: $(cat "$OUT/listener.err")"
        tries=$((tries + 1))
        [ $tries -lt 50 ] || die "the dummy listener did not start (see $OUT/listener.err)"
        sleep 0.1
    done
}

stop_listener() {
    [ -n "$LISTENER_PID" ] || return 0
    kill "$LISTENER_PID" 2>/dev/null
    wait "$LISTENER_PID" 2>/dev/null
    LISTENER_PID=""
}

report_new_hits() {   # prints each label (browser, probe) the first time it reaches the listener after the apply
    local lines label sec target
    [ -s "$LISTENER_LOG" ] || return 0
    lines=$(awk -F'\t' -v s="$APPLY_START_MS" -v a="$APPLY_DONE_MS" \
        '$1 >= s && $3 != "-" && !seen[$3]++ { printf "%s\t%.1f\t%s\n", $3, ($1 - a) / 1000, $4 }' "$LISTENER_LOG")
    [ -n "$lines" ] || return 0
    while IFS="$TAB" read -r label sec target; do
        case "$ANNOUNCED" in *"|$label|"*) continue ;; esac
        ANNOUNCED="$ANNOUNCED$label|"
        printf '  + %-18s reached the listener %+.1f s after the apply finished (%s)\n' "$label" "$sec" "$target"
    done <<EOF
$lines
EOF
}

browser_table() {
    [ -s "$LISTENER_LOG" ] || { echo "   (nothing reached the listener)"; return; }
    awk -F'\t' -v s="$APPLY_START_MS" -v a="$APPLY_DONE_MS" -v r="$RESTORE_DONE_MS" '
        $3 == "-" || $1 < s { next }
        { if (!($3 in first)) { first[$3] = $1; order[++n] = $3 } last[$3] = $1; count[$3]++ }
        END {
            if (n == 0) print "   (no browser and no probe reached it: nothing picked the proxy up)"
            for (i = 1; i <= n; i++) {
                b = order[i]
                line = sprintf("   %-18s first %+6.1f s after the apply, %5d requests", b, (first[b] - a) / 1000, count[b])
                if (r > 0) line = line sprintf(", last %+6.1f s after the restore", (last[b] - r) / 1000)
                print line
            }
        }' "$LISTENER_LOG"
}

other_hosts() {
    [ -s "$LISTENER_LOG" ] || return 0
    awk -F'\t' '$3 == "-" && $2 != "direct" { h = $4; sub(/\/.*/, "", h); n[h]++ } END { for (h in n) printf "%7d  %s\n", n[h], h }' \
        "$LISTENER_LOG" | sort -rn
}

# --- talking to the person running it ----------------------------------------------------------------------------

ask_count() {   # ask_count WHAT -> the number typed, or "not answered"
    local ans=""
    if [ "$INTERACTIVE" = 1 ]; then
        printf '\n  How many password or authorisation dialogs did macOS show during the %s? Type 0 if none: ' "$1" >&2
        read -r ans
    fi
    case "$ans" in ''|*[!0-9]*) echo "not answered" ;; *) echo "$ans" ;; esac
}

had_trouble() { awk -F'\t' '$1 == "apply" && ($6 == "slow" || $6 == "failed") { found = 1 } END { exit !found }' "$CMDLOG"; }

hold() {
    local end junk
    if [ "$HOLD" -gt 0 ]; then
        echo "  Holding for $HOLD s. Enter or Ctrl-C restores sooner."
        end=$(( $(date +%s) + HOLD ))
        while [ "$(date +%s)" -lt "$end" ]; do
            report_new_hits
            if [ "$INTERACTIVE" = 1 ]; then read -r -t 2 junk && break; else sleep 2; fi
        done
    else
        echo "  Press Enter to restore (Ctrl-C restores too)."
        while :; do
            report_new_hits
            read -r -t 2 junk && break
        done
    fi
    report_new_hits
}

# --- the summary -------------------------------------------------------------------------------------------------

calls_made() { [ -f "$CMDLOG" ] && [ "$(wc -l < "$CMDLOG")" -gt 1 ]; }

command_table() {
    calls_made || { echo "   (no calls made)"; return; }
    awk -F'\t' 'NR > 1 {
        out = $7; if (length(out) > 60) out = substr(out, 1, 57) "..."
        printf "   %-7s %-18s %-26s %-6s %6s ms  rc=%s  %s\n", $1, $2, $3, $6, $5, $4, out
    }' "$CMDLOG"
}

is_positive() { is_number "$1" && [ "$1" -gt 0 ]; }

s1_verdict() {
    local failed slow
    calls_made || { echo "nothing was applied"; return; }
    failed=$(awk -F'\t' 'NR > 1 && $6 == "failed" { print $3 }' "$CMDLOG" | sort -u | tr '\n' ' ')
    slow=$(awk -F'\t' 'NR > 1 && $6 == "slow" { print $3 }' "$CMDLOG" | sort -u | tr '\n' ' ')
    if [ -n "$failed" ]; then
        echo "networksetup FAILED on this $ACCOUNT account: $failed"
    elif [ -n "$slow" ] || is_positive "$DIALOGS_APPLY" || is_positive "$DIALOGS_RESTORE"; then
        echo "macOS ASKED for authorisation on this $ACCOUNT account (slow calls: ${slow:-none})"
    else
        echo "every call ran SILENTLY on this $ACCOUNT account"
    fi
}

write_summary() {
    local i names=""
    for i in "${ACTIVE[@]}"; do names="$names${names:+, }${SVC[$i]}"; done
    {
        echo "Josour spike S1: the macOS system proxy (ADR-0015)"
        echo "run     : $1, $(date '+%Y-%m-%d %H:%M:%S')"
        echo "macOS   : $MACOS"
        echo "account : $ACCOUNT"
        echo "port    : $PORT"
        echo "bypass  : ${BYPASS_LIST[*]}"
        echo "touched : $names"
        [ ${#SKIPPED[@]} -eq 0 ] || printf 'skipped : %s\n' "${SKIPPED[@]}"
        [ ${#DISABLED[@]} -eq 0 ] || echo "disabled (left alone): $(IFS=,; echo "${DISABLED[*]}")"
        echo
        echo "1. networksetup, call by call (slow = over $PROMPT_MS ms, in practice a dialog on screen)"
        command_table
        echo "   dialogs you counted: apply = ${DIALOGS_APPLY:-n/a}, restore = ${DIALOGS_RESTORE:-n/a}"
        echo "   => $(s1_verdict)"
        echo
        echo "2. read-back"
        echo "   after the apply  : ${APPLY_READBACK:-not reached}"
        [ -z "$APPLY_DIFF" ] || printf '%s' "$APPLY_DIFF"
        echo "   scutil --proxy showed the change ${SCUTIL_APPLY:-n/a} after the last apply call, and let go of it ${SCUTIL_RESTORE:-n/a} after the last restore call"
        echo "   after the restore: ${RESTORE_READBACK:-not reached}"
        [ -z "$RESTORE_DIFF" ] || printf '%s' "$RESTORE_DIFF"
        echo
        echo "3. who used the proxy (the dummy listener; seconds after the end of the apply and of the restore)"
        browser_table
        echo "   other requests that reached the listener: $(other_hosts | awk '{ n += $1 } END { print n + 0 }') (by host: listener-hosts.txt)"
        echo
        echo "4. before: the snapshot the restore put back"
        for i in "${ACTIVE[@]}"; do describe_tsv "${SNAP[$i]}"; done
    } > "$SUMMARY"
    other_hosts > "$OUT/listener-hosts.txt"
    echo
    echo "================ $SUMMARY ================"
    cat "$SUMMARY"
    echo
    echo "Send back summary.txt. It names your network services and your previous proxy settings, and"
    echo "listener-hosts.txt lists the hosts other apps tried to reach; look through them before sharing."
}

finish() {   # runs once, from the end of the script or from the EXIT trap
    [ "$FINISHED" = 1 ] && return
    FINISHED=1
    trap '' INT TERM HUP
    if [ "$APPLIED" = 1 ] && [ "$RESTORED" = 0 ]; then
        echo
        echo "== restoring after an interruption =="
        restore_all
        RESTORE_DONE_MS=$(now_ms)
        RESTORED=1
        verify_restored
        DIALOGS_RESTORE="not asked (interrupted)"
    fi
    stop_listener
    write_summary "$1"
}

# --- restore mode ------------------------------------------------------------------------------------------------

if [ -n "$RESTORE_FILE" ]; then
    [ -f "$RESTORE_FILE" ] || die "no such snapshot: $RESTORE_FILE"
    load_snapshot "$RESTORE_FILE"
    [ ${#ACTIVE[@]} -gt 0 ] || die "no service in $RESTORE_FILE"
    SNAPSHOT="$RESTORE_FILE"
    OUT="$(cd "$(dirname "$RESTORE_FILE")" && pwd)/restore-$(date +%Y%m%d-%H%M%S)"
    mkdir -p "$OUT" || die "cannot create $OUT"
    CMDLOG="$OUT/commands.tsv"
    printf 'phase\tservice\tcommand\trc\tms\tverdict\toutput\n' > "$CMDLOG"
    trap '' INT TERM HUP
    echo "== restoring from $RESTORE_FILE =="
    restore_all
    echo
    echo "== read back =="
    verify_restored
    echo "  => $RESTORE_READBACK"
    [ "$RESTORE_OK" = 1 ] || exit 2
    exit 0
fi

# --- read what is there ------------------------------------------------------------------------------------------

read_services
snapshot_all

echo "== Josour spike S1: the macOS system proxy =="
echo "  macOS $MACOS, $ACCOUNT account, port $PORT"
[ ${#DISABLED[@]} -eq 0 ] || echo "  disabled services, left alone: $(IFS=,; echo "${DISABLED[*]}")"
for s in "${SKIPPED[@]}"; do echo "  skipped: $s"; done
echo "  current settings of the services it would touch:"
for i in "${ACTIVE[@]}"; do describe_tsv "${SNAP[$i]}"; done

if [ "$DRY_RUN" = 1 ]; then
    echo
    echo "  scutil --proxy (the effective configuration, from the primary service):"
    scutil --proxy 2>&1 | sed 's/^/    /'
    echo
    echo "  it would set 127.0.0.1:$PORT for web and secure web, bypass: ${BYPASS_LIST[*]}"
    if nc -z 127.0.0.1 "$PORT" >/dev/null 2>&1; then echo "  port $PORT is IN USE: pick another --port"; else echo "  port $PORT is free"; fi
    echo
    echo "Dry run: nothing was changed."
    exit 0
fi

# --- apply, look, restore ----------------------------------------------------------------------------------------

[ -f "$HERE/dummy-listener.pl" ] && [ -f "$HERE/pages/index.html" ] \
    || die "copy the whole scripts/spikes folder: dummy-listener.pl and pages/ must sit next to this script"
if [ "$INTERACTIVE" = 0 ] && [ "$HOLD" -eq 0 ]; then HOLD=60; echo "  (no terminal to press Enter in: holding for 60 s)"; fi

[ -n "$OUT" ] || OUT="$HOME/josour-spike-results/s1-macos-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUT" || die "cannot create $OUT"
OUT="$(cd "$OUT" && pwd)"
SNAPSHOT="$OUT/snapshot.tsv"
CMDLOG="$OUT/commands.tsv"
LISTENER_LOG="$OUT/listener.log"
SUMMARY="$OUT/summary.txt"
write_snapshot
scutil --proxy > "$OUT/scutil-before.txt" 2>&1
printf 'phase\tservice\tcommand\trc\tms\tverdict\toutput\n' > "$CMDLOG"

trap 'finish interrupted' EXIT
trap 'echo; echo "interrupted"; exit 130' INT TERM HUP
start_listener

echo
echo "== 1. open the poller =="
echo "  Open http://127.0.0.1:$PORT/ in Safari, Chrome, Firefox and Edge, side by side. Red is right for now."
echo "  For the .NET question, start scripts/spikes/proxy-probe in another terminal now (see the runbook)."
echo "  The snapshot is in $SNAPSHOT; if this run is ever killed, restore it with:"
echo "    $0 --restore '$SNAPSHOT'"
if [ "$INTERACTIVE" = 1 ]; then
    printf '  Press Enter to APPLY the proxy (Ctrl-C quits, with nothing changed) '
    read -r junk
fi

echo
echo "== 2. apply =="
APPLIED=1   # before the first change, so an interruption half-way through still restores
APPLY_START_MS=$(now_ms)
asked=0
remaining=${#ACTIVE[@]}
for i in "${ACTIVE[@]}"; do
    apply_service "$i"
    remaining=$((remaining - 1))
    if [ $asked = 0 ] && [ $remaining -gt 0 ] && [ "$INTERACTIVE" = 1 ] && had_trouble; then
        asked=1
        printf '  macOS slowed down or refused those calls. Apply to the other %s service(s) too? [y/N] ' "$remaining"
        read -r ans
        case "$ans" in y|Y|yes|YES) ;; *) echo "  stopping the apply here"; break ;; esac
    fi
done
APPLY_DONE_MS=$(now_ms)
SCUTIL_APPLY=$(wait_scutil ours)
scutil --proxy > "$OUT/scutil-applied.txt" 2>&1
echo "  scutil --proxy showed 127.0.0.1:$PORT $SCUTIL_APPLY after the last call"
DIALOGS_APPLY=$(ask_count "apply")

echo
echo "== 3. read back =="
verify_applied

echo
echo "== 4. look at the browsers =="
hold

echo
echo "== 5. restore =="
trap '' INT TERM HUP   # a second Ctrl-C must not cut the restore short
restore_all
RESTORE_DONE_MS=$(now_ms)
RESTORED=1
trap 'echo; echo "interrupted"; exit 130' INT TERM HUP
SCUTIL_RESTORE=$(wait_scutil not_ours)
scutil --proxy > "$OUT/scutil-after.txt" 2>&1
echo "  scutil --proxy let go of 127.0.0.1:$PORT $SCUTIL_RESTORE after the last call"
DIALOGS_RESTORE=$(ask_count "restore")

echo
echo "== 6. read back =="
verify_restored

if [ "$LINGER" -gt 0 ]; then
    echo
    echo "  The listener stays up $LINGER s more, to catch anything still using the proxy after the restore."
    sleep "$LINGER"
fi

finish complete
[ "$RESTORE_OK" = 1 ] || exit 2
exit 0
