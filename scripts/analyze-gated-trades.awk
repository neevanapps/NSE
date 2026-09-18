#!/usr/bin/awk -f
# Parses the "trade" command's text output (multiple metric sections) and prints, per metric:
# phase split (Open 09:30-10:00 / Mid 10:00-13:30 / Close 13:30-15:15) and DTE split (0-DTE:
# 2026-09-08, 2026-09-15; non-0-DTE: everything else), each as trades/wins/net.
BEGIN {
    metric = ""
    day = ""
}
/^#####/ {
    metric = $0
    next
}
/^2026-09-[0-9][0-9]:/ {
    day = substr($1, 1, length($1)-1)
    next
}
/^    [0-9][0-9]:[0-9][0-9]:[0-9][0-9] / {
    time = $1
    split(time, tparts, ":")
    hh = tparts[1] + 0
    mm = tparts[2] + 0
    minutes = hh * 60 + mm

    for (i = 1; i <= NF; i++) {
        if ($i ~ /^netPnl=/) {
            pnl = substr($i, 8)
            gsub(/^-?/, "", pnl)
            sign = ($i ~ /netPnl=-/) ? -1 : 1
            pnlval = sign * (pnl + 0)
        }
    }

    phase = (minutes < 600) ? "Open" : (minutes < 810) ? "Mid" : "Close"
    dte = (day == "2026-09-08" || day == "2026-09-15") ? "0DTE" : "nonDTE"

    ptrades[metric, phase]++
    pnet[metric, phase] += pnlval
    if (pnlval > 0) pwins[metric, phase]++

    dtrades[metric, dte]++
    dnet[metric, dte] += pnlval
    if (pnlval > 0) dwins[metric, dte]++
}
END {
    for (key in ptrades) {
        split(key, parts, SUBSEP)
        m = parts[1]; p = parts[2]
        printf "%s | phase=%s | trades=%d | wins=%d | winrate=%.1f%% | net=%.2f\n", m, p, ptrades[key], pwins[key], 100.0*pwins[key]/ptrades[key], pnet[key]
    }
    print "---"
    for (key in dtrades) {
        split(key, parts, SUBSEP)
        m = parts[1]; d = parts[2]
        printf "%s | dte=%s | trades=%d | wins=%d | winrate=%.1f%% | net=%.2f\n", m, d, dtrades[key], dwins[key], 100.0*dwins[key]/dtrades[key], dnet[key]
    }
}
