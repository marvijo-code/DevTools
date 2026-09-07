# DevTools

Windows desktop utility for processes, resource history, agent sessions, GitHub workflow runs and disk usage.

## Install and automatic updates

Download `DevTools-win-x64.zip` from [the latest release](https://github.com/marvijo-code/DevTools/releases/latest), extract it, then run:

```powershell
pwsh -NoProfile -File .\Install-DevTools.ps1
```

Windows 11 x64 and PowerShell 7 are required. The package includes the .NET and Windows App SDK runtimes. The repository is private, so GitHub CLI must be installed and `gh auth login` must already authorize access to `marvijo-code/DevTools`.

Open **DevTools** from the Start menu. The launcher checks GitHub automatically in the background on startup, at most once per six hours. A verified newer release is used on the next launch. It never closes the running app. Launching `DevTools.exe` directly bypasses these update checks.

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
