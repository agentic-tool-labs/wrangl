# agent-hierarchy — the version order the shell scripts share. It is the order of Ah.DaemonClient's Versions.Compare; both are
# tested against tests/fixtures/version-order.txt.
#
# ah_version_cmp <a> <b> prints -1, 0 or 1, by semantic-version precedence: the dotted numeric core first (a part that is not
# numeric, or a missing one, counts as 0); a version with a prerelease ranks below the same core without one; then the prerelease
# identifiers left to right, numeric ones numerically, the rest in ASCII order, numeric below the rest, more identifiers higher
# when all else is equal. Build metadata after "+" is ignored.

ah_version_cmp() {
    LC_ALL=C awk -v a="$1" -v b="$2" '
        function parse(s, core, pre, out,    p, c, r, i) {
            sub(/\+.*/, "", s)
            p = index(s, "-")
            if (p > 0) { c = substr(s, 1, p - 1); r = substr(s, p + 1) } else { c = s; r = "" }
            out["nc"] = split(c, core, ".")
            for (i = 1; i <= out["nc"]; i++) core[i] = match(core[i], /^[0-9]+/) ? substr(core[i], RSTART, RLENGTH) + 0 : 0
            out["np"] = (r == "") ? 0 : split(r, pre, ".")
        }
        function numeric(x) { return x ~ /^[0-9]+$/ }
        function idcmp(x, y,    nx, ny) {
            nx = numeric(x); ny = numeric(y)
            if (nx && ny) {
                sub(/^0+/, "", x); sub(/^0+/, "", y)
                if (length(x) != length(y)) return length(x) < length(y) ? -1 : 1
                return x < y ? -1 : (x > y ? 1 : 0)
            }
            if (nx != ny) return nx ? -1 : 1
            x = x ""; y = y ""
            return (x "") < (y "") ? -1 : ((x "") > (y "") ? 1 : 0)
        }
        BEGIN {
            parse(a, ca, pa, oa); parse(b, cb, pb, ob)
            n = oa["nc"] > ob["nc"] ? oa["nc"] : ob["nc"]
            for (i = 1; i <= n; i++) {
                x = (i <= oa["nc"]) ? ca[i] : 0; y = (i <= ob["nc"]) ? cb[i] : 0
                if (x != y) { print (x < y ? -1 : 1); exit }
            }
            if (oa["np"] == 0 || ob["np"] == 0) {
                print (oa["np"] == ob["np"]) ? 0 : (oa["np"] == 0 ? 1 : -1); exit
            }
            m = oa["np"] < ob["np"] ? oa["np"] : ob["np"]
            for (i = 1; i <= m; i++) {
                c = idcmp(pa[i], pb[i])
                if (c != 0) { print c; exit }
            }
            print (oa["np"] == ob["np"]) ? 0 : (oa["np"] < ob["np"] ? -1 : 1)
        }'
}
