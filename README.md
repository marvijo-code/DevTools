# DevTools

Windows desktop utility for processes, resource history, agent sessions, GitHub workflow runs and disk usage.

## Install and automatic updates

Download `DevTools-win-x64.zip` from [the latest release](https://github.com/marvijo-code/DevTools/releases/latest), extract it, then run:

```powershell
pwsh -NoProfile -File .\Install-DevTools.ps1
```

Windows 11 x64 and PowerShell 7 are required. The package includes the .NET and Windows App SDK runtimes. The repository is private, so GitHub CLI must be installed and `gh auth login` must already authorize access to `marvijo-code/DevTools`.

Open **DevTools** from the Start menu. The launcher checks GitHub automatically in the background on startup, at most once per six hours. A verified newer release is used on the next launch. It never closes the running app. Launching `DevTools.exe` directly bypasses these update checks.

### Administrator rights

DevTools starts **as administrator by default**, because the fast NTFS folder scan needs the raw-volume handle (see Storage below). Windows asks for approval each time.

To change it, open the Storage tab and use the **Startup and admin rights** toggle. The choice is stored in `%LOCALAPPDATA%\DevTools\settings.json` as `RunAsAdministrator` and applies to the next launch from the Start menu. A missing file or a missing property keeps the administrator default, so an older settings file never silently turns elevation off.

Two Start-menu shortcuts are installed:

- **DevTools** - administrator, the default.
- **DevTools (no admin)** - no administrator rights for that one launch, without changing the stored setting. Folder scans use the slower engine.

The updater verifies the package SHA-256 and release identity before changing the active version. Downloads and extraction happen in a separate directory. A failed update leaves the previous version selected. Older version folders remain available under `%LOCALAPPDATA%\MarvijoDevTools\versions`. Update results are saved in `%LOCALAPPDATA%\MarvijoDevTools\update-state.json`.

## Processes and System Informer

The navigation rail uses consistent button widths and indicates the active workspace. Both content panes have equal width. The right pane follows the active workspace: selected process details in Processes, selected workflow details in Runs, disk information in Storage, and the machine overview in Resources. Click a process row or its Details button to select it. Filtering away a selected row clears its details instead of substituting another item. Background run updates preserve the user's selection.

DevTools collects process memory from Windows and groups processes by application or agent session. The list includes all enumerated processes, without the previous 40-process cap. Protected processes can lack executable paths and command lines.

Click **Inspect** to select that process in the installed official System Informer. Use its process properties for threads, handles, modules and connections. This is a separate executable integration; it does not load a custom plugin or replace System Informer's driver. System Informer is not the source of DevTools' memory readings or browser tab names. Install and update the official application separately through its own updater.

Click **Process**, **Start Path**, or **Memory** column headings to sort the groups and their rows. Click the same heading again to reverse the order. The current direction appears above the list. Search text, sort column, direction and expanded/collapsed groups are stored in `%LOCALAPPDATA%\DevTools\settings.json`, alongside the existing app settings.

Start Path shows the executable and its recognized role: page renderer, graphics, network, audio, camera, Node.js utility, Firefox content or media decoder, and Codex app server. Unknown arguments are not given invented meanings. Hover the path to read the full command line. A Firefox content process can hold more than one tab; a process role does not identify a tab title.

## Firefox tab names

Firefox's developer connection is optional and must be explicitly enabled in Firefox. DevTools does not change your Firefox profile or launch arguments.

1. Enable browser chrome/add-on debugging and remote debugging in Firefox's developer tool settings.
2. Keep `devtools.debugger.force-local` enabled. Start Firefox with `--start-debugger-server 63419` after closing it normally. Firefox may request connection approval.
3. In DevTools, click **Connect Firefox**, enter that port and approve the connection in Firefox if asked.

This developer connection grants privileged browser access. Enable it only while you intend to use the integration. DevTools connects only to `127.0.0.1`, sends a fixed read-only query, and excludes private and unloaded tabs. Reconnect after restarting either application. A disconnected or unsupported browser returns to the ordinary process view.

Tabs sharing a process are listed together against that process's memory once. The value is process working set, not an exact independent measurement for each tab or its subframes. The bridge uses Firefox's developer protocol and may need adjustment after a Firefox update.

## Storage and largest folders

The Storage workspace sizes **every folder on every fixed drive** once, in the background, and keeps the result in memory as a folder tree. From that one scan it shows:

- **Largest Folders - X:\\** for each drive: the 25 largest top-level folders, each with its 15 largest subfolders behind the **+** button.
- **Folder Browser (all drives)**: pick a drive, click any folder to open it, **Up** to go back. Every level comes from the in-memory tree, so it opens instantly.
- User Profile, AppData, Repositories, Installed Software, Temp Hotspots, Docker and Drive Summary, all read from the same tree.

The window opens at once and the scan never runs on the UI thread. Each drive uses the fastest engine that works, with a time limit on each step and a fall-through to the next:

1. **Everything** (voidtools) with *Index folder size* turned on: one short list of folders, no disk reads. Probe: `es.exe -get-everything-version` must answer within 2 s.
2. **NTFS Master File Table** when DevTools runs as administrator (the default). Measured 48 s for a 950 GB C: with 7.5 million files.
3. **Everything without folder sizes**: every file from the Everything index (measured 81 s for C: with free RAM; it gives up when it slows below 50,000 results a minute, which happens when Everything is paged out on a low-memory laptop).
4. **Folder walk**: a low-priority walk with at most 4 threads. It is the slow path: gdu, dua and diskus each needed more than 15 minutes for C: on this laptop.

`es.exe` is found in the app folder, `Program Files\Everything`, `~\.local\bin`, `~\bin`, the winget links folder or `PATH`. Each drive section names the engine and time, and the log records `Drive C:\: <engine> sized N folders ... in Xs.` Start with `--minimized` to open on the taskbar without taking focus.

The **Biggest Files** card below it lists the largest individual files. It uses the same fast NTFS path when elevated.

### Bulk cleanup

The **Bulk Cleanup** card runs the reviewed laptop cleanup at `C:\dev\ai-tools\scripts\free-up-disk-space.ps1`. Clicking **Bulk Cleanup PC** first performs a dry run, shows what would go and the total reclaimable size, and only deletes after you confirm. It needs PowerShell 7; the card says so when `pwsh.exe` or the script is missing.

Four lanes run, in this order:

- **Caches** - npm, bun, yarn, pip and NuGet caches, plus Codex telemetry log DBs. All of it re-downloads.
- **BuildArtifacts** - `node_modules`, `bin`, `obj`, `.venv`, `dist`, `target` and similar output, in stale checkouts only. Active checkouts are never swept.
- **PublishedVideos** - everything except the deliverable inside video episodes under `C:\dev\video-episodes` that are proven published. That removes `work\`, `runs\`, `node_modules\`, `video\`, `captures\`, `audio\`, `slides\`, `review\`, `edit\`, `ledgers\`, `prompts\`, `scripts\`, `transcript\`, `verification\`, `toolkit\`, `vps-setup\` and `shorts\`. It keeps `outputs\` (the master video), `release\` (thumbnails, captions, upload result), `episode.json` and every top-level file.
- **GitDebris** - crashed git writes (`objects/**/tmp_obj_*`, which `git gc` itself reports as garbage), stale `.git` `*.lock` files older than one hour, and `refs/t3/checkpoints` agent restore points older than 30 days, followed by `git gc --prune=now` per touched repo. Branches, tags, stash, remotes and worktree links are never touched; history is never rewritten. A repo with a live git writer is skipped. Lane test: `pwsh -NoProfile -File C:\dev\ai-tools\scripts\free-up-disk-space.GitDebris.Tests.ps1`.

An episode is proven published only when `episode.json` carries a `published_url` or `release_state.youtube_public`, or when `release\upload-result.json` records a successful upload, or when the episode id is listed in `C:\dev\ai-tools\scripts\published-video-episodes.json`. Two guards make the lane safe: the episode must hold an `.mp4` master under `outputs\`, and no direct child may have changed within the last seven days. Nothing is deleted when a guard fails.

The same script runs daily at 11:00 through the **DevBox Disk Cleanup** scheduled task, which logs to `C:\dev\ai-tools\logs\disk-cleanup.log`. The button and the task therefore never drift apart. To preview without deleting, run `pwsh -NoProfile -File C:\dev\ai-tools\scripts\run-disk-cleanup.ps1 -Preview`.

## Build and verification

```powershell
dotnet test tests\DiskUsageAnalyzer.Tests\DiskUsageAnalyzer.Tests.csproj -m:1 -p:UseSharedCompilation=false
pwsh -NoProfile -File distribution\Publish-DevTools.ps1 -Version v1.0.0
```

The packaging script creates `artifacts\releases\v1.0.0\DevTools-win-x64.zip` and `SHA256SUMS.txt`; it does not upload them. Commit source changes before packaging so `release.json` records the correct source commit.

The optional live test requires a separate, already running Firefox verification profile with a local debugger and at least one loaded web page:

```powershell
$env:RUN_LIVE_TESTS = '1'
$env:DEVTOOLS_TEST_FIREFOX_PORT = '63419'
dotnet test tests\DiskUsageAnalyzer.Tests\DiskUsageAnalyzer.Tests.csproj --filter FullyQualifiedName~LiveProcessIntegrationTests
```

The live test reads real Firefox tab names and checks that they join exactly once to live Windows process memory. It does not start, stop or modify your normal Firefox session.

## Implementation references

- [Firefox process model](https://firefox-source-docs.mozilla.org/dom/ipc/process_model.html) explains content, graphics, network and utility processes.
- [Firefox command-line parameters](https://firefox-source-docs.mozilla.org/browser/CommandLineParameters.html) documents the developer server switch.
- [Electron process model](https://www.electronjs.org/docs/latest/tutorial/process-model) explains the main, renderer and utility roles used by Electron applications.
- [System Informer command-line implementation](https://github.com/winsiderss/systeminformer/blob/master/SystemInformer/main.c) defines `-selectpid` and `-selecttab`.
