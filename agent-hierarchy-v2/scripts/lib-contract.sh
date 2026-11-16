# agent-hierarchy — the contract sidecar the shell scripts share.
#
# ah_write_contract_sidecar <binary> <version> <contract> writes <binary>.contract, {"version":V,"contract":C}, by writing a
# temporary file beside it and renaming it over the sidecar, so a reader sees the old file or the new one and never a partial one.
# scripts/install.sh writes the installed pair's sidecars with it, and scripts/publish-native.sh the published pair's. It writes
# nothing and returns 2 when the version is empty or the contract is not a whole number.

ah_write_contract_sidecar() {
    local binary="$1" version="$2" contract="$3"
    [ -n "$version" ] || { echo "ah_write_contract_sidecar: no version for $binary.contract" >&2; return 2; }
    case "$contract" in
        '' | *[!0-9]*) echo "ah_write_contract_sidecar: the contract for $binary.contract is not a number: '$contract'" >&2; return 2 ;;
    esac
    printf '{"version":"%s","contract":%s}\n' "$version" "$contract" > "$binary.contract.$$.tmp" && mv -f "$binary.contract.$$.tmp" "$binary.contract"
}
