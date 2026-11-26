# TabTidy

Close a batch of Windows 11 Notepad tabs with one decision. Double-click `TabTidy.exe` to see how many tabs are open across your Notepad windows.

If Notepad is closed, TabTidy displays “No Notepad found open.” and waits for Enter. List mode and redirected commands exit immediately.

If every open tab is unmodified, TabTidy displays “Notepad is open. There are no changes to save or discard.” You can still use the menu to close tabs or export copies.

| Choice | What happens |
| --- | --- |
| **1. Save all to a folder** | Enter a destination. TabTidy uses Notepad’s **Save as** command for each tab and checks that the file exists before closing it. |
| **2. Discard all** | Type `DISCARD` once. TabTidy uses Notepad’s **Close other tabs** command, answers **Don’t save** for unsaved tabs, then closes the last tab. Unsaved edits are lost. |
| **3. Pick tabs to keep** | Enter tab numbers or ranges such as `2,5,8-10`. Then choose save or discard for the remaining tabs. |
| **Esc or Q** | Exit at the main menu. |

Exports use the filename suggested by Notepad when it includes an extension. Other tabs get names such as `Untitled-001.txt`. A collision adds a suffix such as ` (2)`. The destination directory is created if needed. Leave the Notepad windows idle while a batch runs.

## Command line

Run these commands from the TabTidy folder in PowerShell.

```powershell
.\TabTidy.exe              # Interactive menu
.\TabTidy.exe --list       # List numbered tabs and window handles
.\TabTidy.exe --window 123456  # Manage just the window with this handle
```

TabTidy recognizes Notepad’s “Cannot find … file.” alert and presses **OK** while processing a window. The batch reports how many selected tabs it verified as closed. If an unexpected dialog blocks the operation or a save cannot be verified, it reports the error for that window. Completed exports stay in the destination folder. Exit code 1 indicates a failure or a requested window that could not be found.

## Discard speed

**Discard all** processes up to two windows concurrently. Notepad still presents a separate dialog for each modified tab. The number of unsaved tabs in the busiest window affects the total time.

On the development machine, the bulk-close operation closed 87 unmodified tabs in 3.94 seconds and 15 modified tabs in 9.63 seconds. Those two windows took 14.12 seconds end to end when tested sequentially. The concurrent version has been build-checked. Its end-to-end time on the same workload has yet to be measured. Saving to a folder uses a separate native **Save as** dialog for each tab.

## Build

Run `build.cmd` from the source folder to compile `TabTidy.cs` with the Windows .NET Framework C# compiler and the .NET Framework SDK’s `TlbImp.exe`. The build imports Windows UI Automation from the local system and embeds the required interfaces in the executable. The packaged executable runs independently of the SDK. It targets **English-language, tabbed Windows 11 Notepad**. Run it under the same Windows account and privilege level as Notepad.
