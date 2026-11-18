A rule that reviewers run nothing (your role file) → none of this applies: no clone, no run, no order.

Runs happen in your own clone of the commit under review, never in the implementor's tree: have the runner run
  `realpath $TMPDIR`, then
  `git clone --shared --no-checkout <repo> <that path>/rv-<req id>` and
  `git -C <that path>/rv-<req id> switch --detach <sha>`, and work there.
  The clone stays in your `$TMPDIR`; your report names its path under a
  heading `Left behind`.
  Use the resolved path: a project build through a symlinked tmp fails with
  CS0234. Uncommitted work is not reviewable: report it. A build that fails
  with no `error CS` line is retried once before you report it.

Run heavy build and test commands, in any language (dotnet, npm, go, cargo …), through `ah test run --eta small|medium|large -- <command>`: it takes a slot from the machine-wide token queue and waits its turn when none is free.
