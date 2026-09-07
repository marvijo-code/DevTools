# Windows launch and taskbar identity

The installed entry point is `%LOCALAPPDATA%\MarvijoDevTools\DevTools.Launcher.exe`.
It invokes the installed start script, which resolves `current.json` and launches
the current release. Updates retain version folders for rollback. Shortcuts must
never target an executable inside a version folder.

The Start menu shortcut, existing DevTools taskbar shortcut, and installed app
window use the same `Marvijo.DevTools` AppUserModelID. Window relaunch properties
point to the permanent launcher and `DevTools.ico`. This also applies when a user
pins the running window instead of the Start menu shortcut. Development builds
outside an installed version folder do not acquire this identity.

`Register-DevTools.ps1` installs the shared launch files, creates the Start menu
shortcut, and repairs an existing DevTools pin. The installer and updater both
call registration. It does not add a taskbar pin or change other applications.
`Get-StartApps` should list DevTools with the stable ID after registration.

Windows Search file indexing is independent of these installation files. The
installer does not start WSearch, change its startup type, or rebuild an index.
Windows Start search behavior must be checked on the host; the Start menu entry
and permanent launcher also provide direct access when search is unavailable.

The small launcher uses the Windows .NET Framework runtime. The release publisher
compiles it with the Windows Framework64 compiler and embeds the app icon.
