using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

internal static class TabTidy
{
    // A context menu can take focus from another window's menu. Serialize only
    // the brief menu operations; each window handles its own dialogs independently.
    private static readonly object MenuLock = new object();
    private sealed class Tab
    {
        public IntPtr Window;
        public int ProcessId;
        public string Key;
        public string Title;
        public int Number;
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint relation);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int capacity);
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SelectText(IntPtr edit, uint message, IntPtr start, IntPtr end, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr ReplaceText(IntPtr edit, uint message, IntPtr undo, string text, uint flags, uint timeout, out IntPtr result);

    private static List<IntPtr> Windows()
    {
        var windows = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr unused) {
            if (IsWindowVisible(h)) windows.Add(h);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static string ClassName(IntPtr h)
    {
        var name = new StringBuilder(160);
        GetClassName(h, name, name.Capacity);
        return name.ToString();
    }

    private static Condition Is(AutomationProperty property, object value)
    {
        return new PropertyCondition(property, value);
    }

    private static AutomationElement Find(AutomationElement parent, Condition condition)
    {
        return parent.FindFirst(TreeScope.Descendants, condition);
    }

    private static Condition OfType(ControlType type) { return Is(AutomationElement.ControlTypeProperty, type); }
    private static Condition Id(string id) { return Is(AutomationElement.AutomationIdProperty, id); }

    private static AutomationElement Strip(AutomationElement root)
    {
        return Find(root, new AndCondition(OfType(ControlType.Tab), Id("Tabs")));
    }

    private static string Key(AutomationElement e) { return String.Join(".", e.GetRuntimeId()); }

    private static List<Tab> Inventory(long? only)
    {
        int windowCount;
        return Inventory(only, out windowCount);
    }

    private static List<Tab> Inventory(long? only, out int windowCount)
    {
        var tabs = new List<Tab>();
        windowCount = 0;
        foreach (IntPtr h in Windows())
        {
            if (only.HasValue && h.ToInt64() != only.Value) continue;
            if (!String.Equals(ClassName(h), "Notepad", StringComparison.OrdinalIgnoreCase)) continue;
            int pid;
            GetWindowThreadProcessId(h, out pid);
            using (var p = Process.GetProcessById(pid))
                if (!String.Equals(p.ProcessName, "Notepad", StringComparison.OrdinalIgnoreCase)) continue;
            windowCount++;
            var strip = Strip(AutomationElement.FromHandle(h));
            if (strip == null) throw new InvalidOperationException("Cannot read the tab strip in window " + h + ". Restore the window and dismiss any dialogs.");
            foreach (AutomationElement e in strip.FindAll(TreeScope.Descendants, OfType(ControlType.TabItem)))
                tabs.Add(new Tab { Window = h, ProcessId = pid, Key = Key(e), Title = e.Current.Name, Number = tabs.Count + 1 });
        }
        return tabs;
    }

    private static bool SameWindow(Tab tab)
    {
        int pid;
        return IsWindow(tab.Window) && GetWindowThreadProcessId(tab.Window, out pid) != 0 && pid == tab.ProcessId;
    }

    private static AutomationElement Resolve(Tab tab)
    {
        if (!SameWindow(tab)) return null;
        var strip = WaitFor(() => SameWindow(tab) ? Strip(AutomationElement.FromHandle(tab.Window)) : null,
            "Notepad's tab strip did not become available.");
        return strip.FindAll(TreeScope.Descendants, OfType(ControlType.TabItem))
                    .Cast<AutomationElement>().SingleOrDefault(e => Key(e) == tab.Key);
    }

    private static T WaitFor<T>(Func<T> probe, string message) where T : class
    {
        var timer = Stopwatch.StartNew();
        do {
            try { T value = probe(); if (value != null) return value; }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(100);
        } while (timer.ElapsedMilliseconds < 8000);
        throw new InvalidOperationException(message);
    }

    private static void Invoke(AutomationElement control)
    {
        ((InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    private static AutomationElement MissingFileButton(AutomationElement root)
    {
        var dialog = Find(root, new AndCondition(OfType(ControlType.Window), Is(AutomationElement.NameProperty, "Notepad")));
        if (dialog == null) return null;
        var messages = dialog.FindAll(TreeScope.Descendants, OfType(ControlType.Text));
        if (!messages.Cast<AutomationElement>().Any(e => e.Current.Name.StartsWith("Cannot find ", StringComparison.Ordinal)
            && e.Current.Name.EndsWith(" file.", StringComparison.Ordinal))) return null;
        return Find(dialog, new AndCondition(OfType(ControlType.Button), Id("CloseButton"), Is(AutomationElement.NameProperty, "OK")));
    }

    private static bool DismissMissing(AutomationElement root)
    {
        var ok = MissingFileButton(root);
        if (ok == null || !ok.Current.IsEnabled || ok.Current.IsOffscreen) return false;
        Invoke(ok);
        return true;
    }

    private static bool Selected(AutomationElement e)
    {
        return e != null && ((SelectionItemPattern)e.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;
    }

    private static AutomationElement MenuCommand(Tab tab, string name)
    {
        var root = AutomationElement.FromHandle(tab.Window);
        bool opened = false;
        var item = WaitFor(() => {
            if (DismissMissing(root)) { opened = false; return null; }
            if (!opened)
            {
                var file = Find(root, new AndCondition(OfType(ControlType.MenuItem), Id("File")));
                if (file == null || !file.Current.IsEnabled) return null;
                object expand;
                if (file.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expand))
                    ((ExpandCollapsePattern)expand).Expand();
                else Invoke(file);
                opened = true;
            }
            var menuItems = root.FindAll(TreeScope.Descendants, OfType(ControlType.MenuItem));
            return menuItems.Cast<AutomationElement>().FirstOrDefault(e => e.Current.IsEnabled &&
                !e.Current.IsOffscreen && String.Equals(e.Current.Name, name, StringComparison.OrdinalIgnoreCase));
        }, "Could not find File > " + name + ". An English Notepad interface is required.");
        if (!Selected(Resolve(tab))) throw new InvalidOperationException("The selected tab changed.");
        return item;
    }

    private static IEnumerable<IntPtr> OwnedDialogs(Tab tab)
    {
        return Windows().Where(h => ClassName(h) == "#32770" && GetWindow(h, 4) == tab.Window);
    }

    private static string ChoosePath(string directory, Tab tab, string suggestion, string defaultExtension)
    {
        string stem = "Untitled-" + tab.Number.ToString("000");
        string ext = defaultExtension;
        string leaf = Path.GetFileName(suggestion.Trim().Trim('"'));
        // A native suggestion with an extension can supply an existing file's name.
        if (!String.IsNullOrEmpty(leaf) && Path.HasExtension(leaf) &&
            leaf.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !leaf.EndsWith("."))
        {
            ext = Path.GetExtension(leaf);
            stem = Path.GetFileNameWithoutExtension(leaf);
        }
        if (stem.Length > 100) stem = stem.Substring(0, 100);
        if (System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            stem = "_" + stem;
        for (int n = 1; ; n++)
        {
            string suffix = n == 1 ? "" : " (" + n + ")";
            string path = Path.Combine(directory, stem + suffix + ext);
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }

    private static string SaveCopy(Tab tab, string destination)
    {
        if (OwnedDialogs(tab).Any()) throw new InvalidOperationException("Dismiss the existing dialog first.");
        Invoke(MenuCommand(tab, "Save as"));
        var dialog = WaitFor(() => OwnedDialogs(tab).Select(AutomationElement.FromHandle)
            .FirstOrDefault(e => String.Equals(e.Current.Name, "Save As", StringComparison.OrdinalIgnoreCase)),
            "Save As did not appear.");
        var filenameBox = Find(dialog, new AndCondition(OfType(ControlType.ComboBox), Id("FileNameControlHost")));
        var field = filenameBox == null ? null : Find(filenameBox, OfType(ControlType.Edit));
        var save = Find(dialog, new AndCondition(OfType(ControlType.Button), Id("1")));
        if (field == null || save == null || !String.Equals(save.Current.Name.Replace("&", ""), "Save", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Could not identify the filename and Save controls.");
        var value = (ValuePattern)field.GetCurrentPattern(ValuePattern.Pattern);
        string suggested = value.Current.Value;
        string title = System.Text.RegularExpressions.Regex.Replace(tab.Title, @"\. (Unmodified|Modified)\.$", "");
        if (String.Equals(suggested, Path.GetFileNameWithoutExtension(title), StringComparison.OrdinalIgnoreCase))
            suggested = title;
        string defaultExtension = ".txt";
        var typeBox = Find(dialog, Id("FileTypeControlHost"));
        object typeValue;
        if (typeBox != null && typeBox.TryGetCurrentPattern(ValuePattern.Pattern, out typeValue))
        {
            var match = System.Text.RegularExpressions.Regex.Match(((ValuePattern)typeValue).Current.Value, @"\*\.(\w{1,10})\b");
            if (match.Success) defaultExtension = "." + match.Groups[1].Value;
        }
        string path = ChoosePath(destination, tab, suggested, defaultExtension);
        field.SetFocus();
        IntPtr edit = new IntPtr(field.Current.NativeWindowHandle), ignored;
        if (ClassName(edit) != "Edit" ||
            SelectText(edit, 0x00B1 /* EM_SETSEL */, IntPtr.Zero, new IntPtr(-1), 2, 2000, out ignored) == IntPtr.Zero ||
            ReplaceText(edit, 0x00C2 /* EM_REPLACESEL */, IntPtr.Zero, path, 2, 2000, out ignored) == IntPtr.Zero)
            throw new IOException("Notepad's filename field did not accept the export path.");
        var boxValue = (ValuePattern)filenameBox.GetCurrentPattern(ValuePattern.Pattern);
        if (value.Current.Value != path || boxValue.Current.Value != path || File.Exists(path)) throw new IOException("The export filename changed or already exists.");
        var dialogHandle = new IntPtr(dialog.Current.NativeWindowHandle);
        var submit = System.Threading.Tasks.Task.Run(() => Invoke(save));
        WaitFor(() => {
            if (submit.IsFaulted) throw submit.Exception.GetBaseException();
            return !IsWindow(dialogHandle) && File.Exists(path) ? path : null;
        },
            "Save As did not finish. Check the dialog and destination.");
        return path;
    }

    private static void CloseTab(Tab tab, bool discard)
    {
        lock (MenuLock) Invoke(MenuCommand(tab, "Close tab"));
        bool answered = false;
        WaitFor(() => {
            if (!SameWindow(tab)) return "closed";
            var root = AutomationElement.FromHandle(tab.Window);
            if (DismissMissing(root)) return null;
            var negative = Find(root, new AndCondition(OfType(ControlType.Button),
                new OrCondition(Id("SecondaryButton"), Is(AutomationElement.NameProperty, "Don't save"))));
            if (negative != null && negative.Current.IsEnabled && !negative.Current.IsOffscreen &&
                String.Equals(negative.Current.Name.Replace('\u2019', '\'').Replace("&", ""), "Don't save", StringComparison.OrdinalIgnoreCase))
            {
                if (!discard) throw new InvalidOperationException("Notepad still reports unsaved changes after export. The tab was left open.");
                if (!answered) { Invoke(negative); answered = true; }
                return null;
            }
            return Resolve(tab) == null ? "closed" : null;
        }, "Notepad did not confirm that the tab closed. Check the window.");
    }

    private static string OpenBulkMenu(IntPtr handle)
    {
        // UIA's native ShowContextMenu targets the selected tab even in a background window.
        var client = (NativeUia.IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("ff48dba4-60ef-4201-aa87-54103eef594e")));
        var descendants = NativeUia.TreeScope.TreeScope_Descendants;
        var root = client.ElementFromHandle(handle);
        var strip = root.FindFirst(descendants, client.CreatePropertyCondition(AutomationElement.AutomationIdProperty.Id, "Tabs"));
        var selected = strip == null ? null : strip.FindFirst(descendants,
            client.CreatePropertyCondition(SelectionItemPattern.IsSelectedProperty.Id, true));
        if (selected == null) throw new InvalidOperationException("Cannot identify the active Notepad tab.");
        var key = String.Join(".", selected.GetRuntimeId());
        ((NativeUia.IUIAutomationElement3)selected).ShowContextMenu();
        return key;
    }

    private static void DiscardWindow(List<Tab> snapshot)
    {
        var first = snapshot[0];
        if (!SameWindow(first)) throw new InvalidOperationException("The original window is unavailable.");
        var root = AutomationElement.FromHandle(first.Window);
        DismissMissing(root);
        if (OwnedDialogs(first).Any()) throw new InvalidOperationException("A file dialog is already open.");
        var current = Inventory(first.Window.ToInt64());
        var keys = new HashSet<string>(snapshot.Select(t => t.Key));
        if (current.Count == 0 || current.Any(t => !keys.Contains(t.Key)))
            throw new InvalidOperationException("The set of tabs changed. Run TabTidy again.");
        if (current.Count == 1) { CloseTab(current[0], true); return; }

        string anchor;
        lock (MenuLock)
        {
            anchor = WaitFor(() => {
                if (DismissMissing(root)) return null;
                var file = Find(root, new AndCondition(OfType(ControlType.MenuItem), Id("File")));
                return file != null && file.Current.IsEnabled ? OpenBulkMenu(first.Window) : null;
            }, "The tab menu is blocked by a Notepad dialog.");
            if (!keys.Contains(anchor)) throw new InvalidOperationException("The active tab changed.");
            var command = WaitFor(() => Find(root, new AndCondition(OfType(ControlType.MenuItem), Id("CloseOthersMenuItem"))),
                "Notepad's Close other tabs command is unavailable.");
            Invoke(command);
        }
        var inactive = Stopwatch.StartNew();
        var tabItems = OfType(ControlType.TabItem);
        var discardButton = new AndCondition(OfType(ControlType.Button), Id("SecondaryButton"));
        int previous = current.Count;
        while (inactive.ElapsedMilliseconds < 12000)
        {
            if (!SameWindow(first)) return;
            try
            {
                var button = Find(root, discardButton);
                if (button != null && button.Current.IsEnabled && !button.Current.IsOffscreen &&
                    String.Equals(button.Current.Name.Replace('\u2019', '\''), "Don't save", StringComparison.OrdinalIgnoreCase))
                {
                    Invoke(button);
                    // Reset the timeout on observed progress below, never just on a click.
                }
                else if (DismissMissing(root)) { Thread.Sleep(10); }
                var strip = Strip(root);
                if (strip != null)
                {
                    var remaining = strip.FindAll(TreeScope.Descendants, tabItems);
                    if (remaining.Count == 1 && Key(remaining[0]) == anchor)
                    {
                        CloseTab(snapshot.First(t => t.Key == anchor), true);
                        return;
                    }
                    if (remaining.Count < previous) { previous = remaining.Count; inactive.Restart(); }
                }
            }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(12);
        }
        throw new InvalidOperationException("Notepad stopped closing tabs. Check for an unexpected dialog.");
    }

    private static HashSet<int> ParseKeep(string input, int count)
    {
        var keep = new HashSet<int>();
        if (String.IsNullOrWhiteSpace(input)) return keep;
        foreach (string part in input.Split(','))
        {
            string[] pair = part.Trim().Split('-');
            int first, last;
            if (pair.Length > 2 || !Int32.TryParse(pair[0], out first)) throw new FormatException("Enter numbers or ranges such as 2,5,8-10.");
            last = first;
            if (pair.Length == 2 && !Int32.TryParse(pair[1], out last)) throw new FormatException("Invalid range.");
            if (first < 1 || last < first || last > count) throw new FormatException("A tab number is outside the list.");
            for (int i = first; i <= last; i++) keep.Add(i);
        }
        return keep;
    }

    private static string Ask(string prompt)
    {
        Console.Write(prompt + " ");
        string s = Console.ReadLine();
        if (s == null) throw new OperationCanceledException();
        return s.Trim();
    }

    private static string MenuChoice()
    {
        if (Console.IsInputRedirected) return Ask("Choice:");
        Console.Write("Press a choice: ");
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Q) { Console.WriteLine("Cancel"); return "Q"; }
            if ("123".Contains(key.KeyChar)) { Console.WriteLine(key.KeyChar); return key.KeyChar.ToString(); }
        }
    }

    [MTAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            long? only = null;
            bool list = false;
            for (int i = 0; i < args.Length; i++)
            {
                long value;
                if (args[i] == "--window" && i + 1 < args.Length && Int64.TryParse(args[++i], out value)) only = value;
                else if (args[i] == "--list") list = true;
                else if (args[i] == "--help") { Console.WriteLine("TabTidy [--list] [--window HANDLE]\nManage open tabs in English-language Windows 11 Notepad."); return 0; }
                else throw new ArgumentException("Unknown argument. Use --help.");
            }
            int windowCount;
            var tabs = Inventory(only, out windowCount);
            const string noChanges = "Notepad is open. There are no changes to save or discard.";
            if (tabs.Count == 0)
            {
                if (only.HasValue && windowCount == 0)
                {
                    Console.Error.WriteLine("No Notepad tabs found in window " + only.Value + ".");
                    return 1;
                }
                Console.WriteLine(windowCount == 0 ? "No Notepad found open." : noChanges);
                if (!list && !Console.IsInputRedirected && !Console.IsOutputRedirected)
                {
                    Console.Write("Press Enter to close.");
                    Console.ReadLine();
                }
                return 0;
            }
            Console.WriteLine("{0} Notepad tabs in {1} windows.", tabs.Count, windowCount);
            if (tabs.All(t => t.Title.EndsWith(". Unmodified.", StringComparison.Ordinal)))
                Console.WriteLine(noChanges);
            if (list) { Show(tabs); return 0; }
            Console.WriteLine("[1] Save all to a folder and close\n[2] Discard all and close\n[3] Pick tabs to keep\n[Esc/Q] Cancel");
            string action = MenuChoice();
            bool fullWindow = action == "2";
            if (action == "3")
            {
                Show(tabs);
                var keep = ParseKeep(Ask("Tab numbers to keep (example: 2,5,8-10):"), tabs.Count);
                tabs = tabs.Where(t => !keep.Contains(t.Number)).ToList();
                if (tabs.Count == 0) return 0;
                action = Ask("For the remaining " + tabs.Count + " tabs: [1] Save and close  [2] Discard and close  [Q] Cancel:");
            }
            if (action != "1" && action != "2") return 0;
            string directory = null;
            if (action == "1")
            {
                string input = Ask("Destination folder (blank cancels):").Trim('"');
                if (input.Length == 0) return 0;
                directory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(input));
                Directory.CreateDirectory(directory);
            }
            else if (Ask("Type DISCARD to close " + tabs.Count + " tabs and discard unsaved edits:") != "DISCARD") return 0;
            var elapsed = Stopwatch.StartNew();
            int closed = 0;
            if (fullWindow)
            {
                var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 2 };
                System.Threading.Tasks.Parallel.ForEach(tabs.GroupBy(t => t.Window), options, group =>
                {
                    try
                    {
                        DiscardWindow(group.ToList());
                        if (SameWindow(group.First()))
                        {
                            var expected = new HashSet<string>(group.Select(t => t.Key));
                            if (Inventory(group.Key.ToInt64()).Any(t => expected.Contains(t.Key)))
                                throw new InvalidOperationException("Some original tabs are still open.");
                        }
                        Interlocked.Add(ref closed, group.Count());
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("Stopped window {0}: {1}", group.Key, ex.Message);
                        try {
                            var live = Inventory(group.Key.ToInt64());
                            if (live.Count > 0 || !SameWindow(group.First()))
                            {
                                var remaining = new HashSet<string>(live.Select(t => t.Key));
                                Interlocked.Add(ref closed, group.Count(t => !remaining.Contains(t.Key)));
                            }
                        } catch (Exception) { /* Closure could not be verified. */ }
                    }
                });
                Console.WriteLine("Verified {0} of {1} selected tabs closed in {2:0.00} seconds.", closed, tabs.Count, elapsed.Elapsed.TotalSeconds);
                return closed == tabs.Count ? 0 : 1;
            }
            var blocked = new HashSet<IntPtr>();
            foreach (var tab in tabs)
            {
                if (blocked.Contains(tab.Window)) continue;
                try {
                    if (SameWindow(tab)) DismissMissing(AutomationElement.FromHandle(tab.Window));
                    var element = Resolve(tab);
                    if (element == null || element.Current.Name != tab.Title) throw new InvalidOperationException("The tab changed since the initial list.");
                    if (OwnedDialogs(tab).Any()) throw new InvalidOperationException("A dialog is already open.");
                    ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                    WaitFor(() => Selected(Resolve(tab)) ? "selected" : null, "Could not select the tab.");
                    if (directory != null) Console.WriteLine("Saved " + SaveCopy(tab, directory));
                    CloseTab(tab, directory == null);
                    Console.WriteLine("Closed tab " + tab.Number);
                    closed++;
                } catch (Exception ex) {
                    blocked.Add(tab.Window);
                    Console.Error.WriteLine("Stopped window {0} at tab {1}: {2}", tab.Window, tab.Number, ex.Message);
                }
            }
            Console.WriteLine("Verified {0} of {1} selected tabs closed.", closed, tabs.Count);
            return closed == tabs.Count ? 0 : 1;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static void Show(IEnumerable<Tab> tabs)
    {
        foreach (var tab in tabs)
            Console.WriteLine("[{0}] (window {1}) {2}", tab.Number, tab.Window, new string(tab.Title.Select(c => Char.IsControl(c) ? ' ' : c).ToArray()));
    }
}
