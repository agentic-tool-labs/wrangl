#!/bin/bash
# agent-hierarchy — the one place the dev entry points (scripts/dotnet-test.sh, tests/test-native-build.sh and
# tests/test-native-frontend.sh) resolve the checkout root. Source it, then:
#   ROOT="$(ah_tree_root "$(dirname "$0")/..")"
#
# ah_tree_root <dir>: the physical path of <dir>, symlinks resolved. A checkout reached through a symlink (macOS $TMPDIR and /tmp
# are one) otherwise builds under two spellings of one path, which MSBuild treats as two instances of every project; they race on
# the same native/artifacts/obj files and a cold build fails with no compiler error.
ah_tree_root() { (cd "$1" && pwd -P); }
