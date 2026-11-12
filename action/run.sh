#!/usr/bin/env bash
# Runs PackageBat on a pull request. Called by action.yml; all inputs arrive as PB_* env vars
# (never interpolated into the script, so PR content can't inject shell).
set -euo pipefail

work="$RUNNER_TEMP/packagebat"
rm -rf "$work"
mkdir -p "$work"
report="$work/report.md"
json="$work/report.json"
echo "report=$report" >> "$GITHUB_OUTPUT"
echo "report-json=$json" >> "$GITHUB_OUTPUT"

if [[ -z "${PB_BASE:-}" ]]; then
  echo "::error::No base revision. Run on pull_request events or set the base-ref input."
  exit 1
fi

echo "::group::Build PackageBat"
# Build from the action folder so the consumer repo's global.json can't pin an older SDK for us.
(cd "$GITHUB_ACTION_PATH" && dotnet build src/PackageBat/PackageBat.csproj -c Release -o "$work/tool" -v quiet)
echo "::endgroup::"
pb() { dotnet "$work/tool/PackageBat.dll" "$@"; }

# Compare against the merge base so commits that landed on the base branch after the PR was opened don't count.
git fetch --no-tags --quiet origin "$PB_BASE" 2>/dev/null || true
base="$(git merge-base "$PB_BASE" HEAD 2>/dev/null || echo "$PB_BASE")"

pb detect --base "$base" --head HEAD > "$work/bumps.json"
count="$(jq length "$work/bumps.json")"
if [[ "$count" == "0" ]]; then
  echo "No NuGet package version changes in this PR."
  printf 'breaking=0\nused=0\n' >> "$GITHUB_OUTPUT"
  echo "🦇 PackageBat: no NuGet package version changes in this PR." >> "$GITHUB_STEP_SUMMARY"
  exit 0
fi
echo "Version changes:"
jq -r '.[] | "  \(.package) \(.from) -> \(.to)"' "$work/bumps.json"

# Build the code as it was before the bump. It compiled then, and its assemblies + PDBs
# tell us exactly which package APIs it binds to and on which lines.
echo "::group::Build your code at $base (before the bump)"
git worktree add --detach --force "$work/base" "$base" > /dev/null
trap 'git worktree remove --force "$work/base" > /dev/null 2>&1 || true' EXIT
(
  cd "$work/base"
  targets=()
  if [[ -n "${PB_PROJECT:-}" ]]; then
    targets=("$PB_PROJECT")
  else
    # Same lookup as `dotnet build` at the root, then a single solution further down, then every project.
    mapfile -t targets < <(find . -maxdepth 1 \( -name '*.sln' -o -name '*.slnx' -o -name '*.csproj' -o -name '*.fsproj' -o -name '*.vbproj' \) | sort)
    if [[ ${#targets[@]} -ne 1 ]]; then
      mapfile -t targets < <(find . -maxdepth 3 -not -path '*/.*' \( -name '*.sln' -o -name '*.slnx' \) | sort)
    fi
    if [[ ${#targets[@]} -ne 1 ]]; then
      mapfile -t targets < <(find . -not -path '*/.*' \( -name '*.csproj' -o -name '*.fsproj' -o -name '*.vbproj' \) | sort)
    fi
  fi
  if [[ ${#targets[@]} -eq 0 ]]; then
    echo "::error::No .NET solution or project found. Set the 'project' input."
    exit 1
  fi
  for target in "${targets[@]}"; do
    echo "dotnet build $target"
    # shellcheck disable=SC2086 # build-args is intentionally word-split
    dotnet build "$target" -c Debug -p:DebugType=portable -p:TreatWarningsAsErrors=false ${PB_BUILD_ARGS:-}
  done
)
echo "::endgroup::"

server="${GITHUB_SERVER_URL:-https://github.com}"
set +e
pb analyze \
  --bumps "$work/bumps.json" \
  --consumer "$work/base" \
  --repo-root "$work/base" \
  --link-base "$server/$GITHUB_REPOSITORY/blob/$base" \
  --markdown "$report" \
  --json "$json" \
  --fail-on "${PB_FAIL_ON:-none}" \
  --threshold "${PB_THRESHOLD:-0}"
status=$?
set -e

if [[ ! -f "$report" ]]; then
  echo "::error::PackageBat did not produce a report (exit code $status)."
  exit 1
fi

echo "breaking=$(jq '[.packages[].used[] | select(.change.severity == "Breaking")] | length' "$json")" >> "$GITHUB_OUTPUT"
echo "used=$(jq '[.packages[].used[]] | length' "$json")" >> "$GITHUB_OUTPUT"
cat "$report" >> "$GITHUB_STEP_SUMMARY"

if [[ "${PB_COMMENT:-true}" == "true" && -n "${PB_PR:-}" ]]; then
  # One comment per PR, updated in place on every push.
  existing="$(gh api "repos/$GITHUB_REPOSITORY/issues/$PB_PR/comments" --paginate \
    --jq '.[] | select(.body | contains("<!-- packagebat-report -->")) | .id' 2>/dev/null | head -n1 || true)"
  if [[ -n "$existing" ]]; then
    gh api -X PATCH "repos/$GITHUB_REPOSITORY/issues/comments/$existing" -F body=@"$report" > /dev/null \
      || echo "::warning::Could not update the PR comment. Give the workflow 'pull-requests: write'."
  else
    gh api -X POST "repos/$GITHUB_REPOSITORY/issues/$PB_PR/comments" -F body=@"$report" > /dev/null \
      || echo "::warning::Could not comment on the PR. Give the workflow 'pull-requests: write'."
  fi
fi

exit $status
