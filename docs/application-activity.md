# Application activity in Processes

Expand Codex to see the 30 most recently updated local thread titles. They come from the local session index and are not a list of running Windows processes. The renderer name is no longer guessed from the most recent thread. Thread memory stays unavailable because there is no reliable thread-to-process mapping.

Expand Firefox to see tab titles. With the existing developer connection enabled, titles include their shared process IDs. Without that connection, DevTools reads recovery.jsonlz4 from profiles with an active parent.lock. Saved titles show their capture time and may lag the browser. Only current entries in open, non-private windows and tabs are read. Closed tabs, closed windows, and page form data are excluded. DevTools never edits the profile or enables debugging.

Memory belongs to the app group and its nested Processes list. It is not divided or duplicated across thread and tab rows. Searching a title retains its app and shared process information. Selecting a thread, tab, or process displays its own details on the right. The separate Details navigation button has been removed.

Application groups appear before other process groups. Sorting remains available in the nested process table, with an arrow beside the active column. Existing explicit expansion choices are preserved.

Validation includes decoder bounds, private/closed-tab exclusion, selected history entries, search, selection retention, and removal of guessed thread attribution. The local verification on 2026-09-07 found both requested Codex titles and six Firefox saved-session titles. Native screenshots verified both app groups and matching details panes.
## Direct process actions

Expanded child content is indented beneath its application group. Each process row has a visible End button, followed by the additional action menu. Ending a process still requires confirmation and includes its subprocesses.

Firefox tab rows also show End. It is enabled only for a live process link. Saved-session rows keep End disabled and offer Connect Firefox at the group level. The confirmation explains that other tabs may share the process. DevTools checks the live title mapping, Firefox process name, and process start time before acting. Ending a shared process is not the same as closing one browser tab.
