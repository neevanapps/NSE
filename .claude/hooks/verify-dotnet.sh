#!/usr/bin/env bash
# Stop-hook safety net: if this turn plausibly touched a .cs/.fs file under the
# repo, require `dotnet build` (0 warnings) and `dotnet test` to pass clean
# before Claude is allowed to consider the turn done. See CLAUDE.md's
# "dotnet build and dotnet test clean ... before considering any change done."
#
# Scoping note: "touched this turn" is approximated as "still shows up as an
# uncommitted change against HEAD, or as a new untracked file" (tracked+staged
# diff, plus untracked files). This is deliberately git-based rather than
# transcript-based — it's simple, robust across edits spanning several files,
# and it's exactly the state a build/test check should be verifying anyway.
# The one gap: if C#/F# changes get committed within the same turn (before
# Stop fires), git status goes clean and this hook won't catch it — but this
# project's own working process already requires build/test to be clean
# before any commit, so that gap shouldn't matter in practice.

set -u
cd "D:/Projects/NSE" || exit 0

changed_tracked=$(git diff --name-only HEAD -- '*.cs' '*.fs' 2>/dev/null)
changed_untracked=$(git ls-files --others --exclude-standard -- '*.cs' '*.fs' 2>/dev/null)

if [ -z "$changed_tracked" ] && [ -z "$changed_untracked" ]; then
  # Nothing .cs/.fs-shaped changed this turn (doc/config-only turn) — skip.
  exit 0
fi

build_log=$(mktemp)
test_log=$(mktemp)
trap 'rm -f "$build_log" "$test_log"' EXIT

dotnet build >"$build_log" 2>&1
build_exit=$?
warning_count=$(grep -oE '^\s*[0-9]+ Warning\(s\)' "$build_log" | grep -oE '[0-9]+' | head -1)
warning_count=${warning_count:-0}

changed_files=$(printf '%s\n%s\n' "$changed_tracked" "$changed_untracked" | sed '/^$/d' | paste -sd, -)

if [ "$build_exit" -ne 0 ] || [ "$warning_count" -gt 0 ]; then
  tail_log=$(tail -n 60 "$build_log")
  reason=$(printf 'dotnet build did not come back clean (exit %s, %s warning(s)) after changes to: %s. Fix the build before finishing this turn. Last 60 lines of `dotnet build`:\n\n%s' \
    "$build_exit" "$warning_count" "$changed_files" "$tail_log")
  python -c "
import json,sys
print(json.dumps({'decision': 'block', 'reason': sys.argv[1]}))
" "$reason"
  exit 0
fi

dotnet test >"$test_log" 2>&1
test_exit=$?

if [ "$test_exit" -ne 0 ]; then
  tail_log=$(tail -n 60 "$test_log")
  reason=$(printf 'dotnet build was clean, but dotnet test failed (exit %s). Fix the failing test(s) before finishing this turn. Last 60 lines of `dotnet test`:\n\n%s' \
    "$test_exit" "$tail_log")
  python -c "
import json,sys
print(json.dumps({'decision': 'block', 'reason': sys.argv[1]}))
" "$reason"
  exit 0
fi

test_summary=$(grep -E 'Passed!|Failed!|Total:' "$test_log" | tail -1)
python -c "
import json,sys
print(json.dumps({'systemMessage': sys.argv[1]}))
" "dotnet build + test clean. ${test_summary}"
exit 0
