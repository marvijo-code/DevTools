## Execution completion

- Carry the user's requested task through to completion in the same turn whenever the next step is clear and executable.
- Do not stop at diagnosis, partial findings, or "next step" language when you can still implement, verify, and close the task yourself.
- Treat "I'll do that next", "I'll patch that next", or similar handoff wording as incomplete when the implementation and verification are still executable in the same turn.
- Only stop to ask the user when the next action is destructive, changes live runtime state without prior approval, writes to production-like infrastructure without approval, or you are blocked by missing access/credentials that cannot be discovered safely.
- After making changes, always stop and run the relevant tool, command, or verification path for the thing you changed before closing the task.

## Git publishing safety

- Never commit changes unless the user explicitly tells you to commit, using those words, in the current conversation.
- Never push commits, create pull requests, open PRs, or publish branches/remotes unless the user explicitly asks for that action in the current conversation.
- Do not infer permission to commit, push, or create a PR from requests to make code changes, prepare a branch, or get work ready for review.
- If there is any ambiguity about whether the user wants a commit, push, or PR, stop and ask.

## Path clarity

- When working in a git worktree or on a newly created/non-default branch, always show the full absolute path in user-facing responses when referring to the workspace, files, or working location.

## GitHub retrieval

- When retrieving GitHub data such as pull requests, workflow runs, job logs, checks, annotations, commits, or diffs, use the GitHub CLI (`gh`) by default.
- Prefer `gh` over browser scraping, web search, or connector tools for GitHub repository data unless the user explicitly asks for a different source or `gh` is unavailable.
- When investigating GitHub Actions failures, start with `gh run view`, `gh pr view`, `gh pr checks`, and related `gh` commands before trying any other approach.
