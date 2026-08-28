#!/usr/bin/env bash
#
# Puts one generated file onto a long-lived branch and keeps exactly one pull request open for it.
#
# main is governed by a repository ruleset that requires a pull request and signed commits, so a job
# cannot push to it. That much the two callers already knew. What they got wrong is HOW the commit is
# made: a commit created by git on the runner is unsigned, so the pull request they opened satisfied
# the pull-request rule and then failed the signature rule forever. #9 had to be re-signed by hand
# before it could be merged.
#
# A commit created through the contents API is signed by GitHub and verifies, so the pull request is
# actually mergeable. That is the whole reason this does not use git.
#
# Inputs, all required except BOT_PR_TOKEN:
#   BRANCH   the long-lived branch to carry the file
#   FILE     repository-relative path of the generated file
#   MESSAGE  commit message
#   TITLE    pull request title
#   BODY     pull request body
#   GH_TOKEN BOT_PR_TOKEN if the repository has one, else the workflow token — see below
#
# Nothing done with GITHUB_TOKEN can trigger another workflow, so a pull request opened with it
# arrives with no checks and cannot satisfy a required-checks rule. With BOT_PR_TOKEN set to a
# personal access token carrying contents and pull-requests write, the pull request behaves like any
# other. Without it the pull request is still opened and still mergeable, it just has no checks.
set -euo pipefail

: "${BRANCH:?}" "${FILE:?}" "${MESSAGE:?}" "${TITLE:?}" "${BODY:?}" "${GITHUB_REPOSITORY:?}"

if git diff --quiet -- "$FILE"; then
  echo "::notice::$FILE is unchanged"
  exit 0
fi

base=$(git rev-parse HEAD)

# Point the branch at what this run was built from, creating it the first time. Force, because the
# branch carries only ever the newest measurement — its history is not interesting and a merge would
# only produce conflicts against itself.
gh api -X PATCH "repos/$GITHUB_REPOSITORY/git/refs/heads/$BRANCH" -f sha="$base" -F force=true >/dev/null 2>&1 \
  || gh api -X POST "repos/$GITHUB_REPOSITORY/git/refs" -f ref="refs/heads/$BRANCH" -f sha="$base" >/dev/null

# The blob sha of the file as the branch has it, which the contents API wants in order to replace
# rather than create. Empty when the file is not there yet, which is legitimate.
blob=$(gh api "repos/$GITHUB_REPOSITORY/contents/$FILE?ref=$BRANCH" --jq '.sha' 2>/dev/null || true)

args=(-X PUT "repos/$GITHUB_REPOSITORY/contents/$FILE"
      -f message="$MESSAGE" -f branch="$BRANCH" -f content="$(base64 -w0 "$FILE")")
if [ -n "$blob" ]; then
  args+=(-f sha="$blob")
fi
gh api "${args[@]}" --jq '"committed \(.commit.sha[0:8]), verified=\(.commit.verification.verified)"'

if [ -n "$(gh pr list --head "$BRANCH" --state open --json number --jq '.[].number')" ]; then
  echo "::notice::refreshed the open pull request for $BRANCH"
  exit 0
fi

gh pr create --base main --head "$BRANCH" --title "$TITLE" --body "$BODY"
