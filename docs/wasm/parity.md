# WPF viewer feature parity (shared Avalonia UI and browser)

Reference: src/StructuredLogViewer (WPF). Everything is built in StructuredLogViewer.Avalonia.Core, so the desktop Avalonia
head and the browser share it. Source of the list: a name-by-name diff of MainWindow, BuildControl, SearchAndResultsControl,
TextViewerControl and DocumentWell between the WPF and Avalonia code, plus the WPF XAML (menus, tabs, named elements).
The Avalonia column is the status of the shared UI; the browser column says works, or N/A with the reason. Effort: S hours, M a day, L several days.
"to verify" means the code exists but is not yet exercised in the browser.

Counts (Avalonia): present 51, partial 3, missing 10, N/A 1  (total 65)

| Area | Feature | Avalonia | Browser | Effort | Notes |
|---|---|---|---|---|---|
| Main window | File > Open Log (Ctrl+O) | present | works (file picker, drag and drop, ?url=) | - |  |
| Main window | File > Open Graph (.dgml/.graph) | missing | works once ported | M | WPF OpenGraph; needs graph view |
| Main window | File > Reload (F5) | present | N/A: no file path on disk; re-open the log | - | browser reloads from the original ?url= only |
| Main window | File > Save Log As (Ctrl+S) | present | partial: download of a binlog written by the logger | S | verify with big logs in browser |
| Main window | File > Redact Secrets (Ctrl+R) | present | to verify in browser | S | RedactInputControl exists |
| Main window | File > Statistics | present | to verify in browser | S | Build.DisplayStats |
| Main window | Recent Logs / Recent Projects, Clear | present | works (localStorage); recent logs hold names only | S | paths cannot be reopened in a browser |
| Main window | Start Page, welcome screen | present | works | - |  |
| Main window | Build / Rebuild Solution/Project (F6, Shift+F6) | present | N/A: runs MSBuild, a browser cannot start processes | - | to hide |
| Main window | Set MSBuild path | present | N/A: no MSBuild in a browser | - | to hide |
| Main window | Help: Search Syntax, links, About | present | works | S | links open in a new tab |
| Main window | Exit (Alt+F4) | present | N/A: no window to close | - | to hide |
| Main window | Open in VS Code, VS Code variant dropdown, hint bar | missing | N/A: starts a local program | - | desktop-only; port the buttons to the Avalonia head later |
| Main window | Attach binlog (multi-log) button | missing | works if VS Code integration is not needed | S | BuildControl.AttachBinlog exists |
| Main window | Exception panel (shows and copies error text) | present | works | - |  |
| Main window | Ctrl+mouse wheel zoom of the whole UI | present | works | - |  |
| Main window | Ctrl+F / Ctrl+Shift+F global search shortcuts | partial | works | S | Ctrl+F present; check Ctrl+Shift+F |
| Main window | Ctrl+C copy, Ctrl+0 reset zoom | partial | works | S | check both |
| Main window | Window position save/restore | present | N/A: no window | - |  |
| Main window | Auto-update (Squirrel) | N/A (WPF only) | N/A: the browser always runs the deployed version | - |  |
| Main window | Drag and drop of a log file | present | works | - |  |
| Main window | Command line / file association | present | N/A: ?url= instead | - |  |
| Tabs | Log tree (main) | present | works | - |  |
| Tabs | Search Log | present | works | - |  |
| Tabs | Properties and items | present | works | - |  |
| Tabs | Files (embedded) | present | works | - |  |
| Tabs | Find in Files | present | works | - |  |
| Tabs | Favorites | present | works (in memory) | S | persist in localStorage? |
| Tabs | Timeline | present | works | - | ported, bb90a59e6 |
| Tabs | Tracing (zoom, scroll, selection) | missing | works once ported | L | TracingControl.xaml.cs 1490 lines, FastCanvas |
| Tabs | Project References graph | missing | works once ported | L | MSAGL layout is managed; WPF GraphControl 868 + GraphHostControl 414 lines |
| Tabs | Targets graph | missing | works once ported | L | shares the graph control |
| Tabs | NuGet graph | missing | works once ported | M | shares the graph control |
| Tabs | Properties graph | missing | works once ported | M | shares the graph control |
| Tabs | Breadcrumb bar, project context bar | present | works | - |  |
| Tabs | Document well: source tabs, close, context | present | works | - |  |
| Context menu | Add/Remove Favorites | present | works | - |  |
| Context menu | View source, View full text, View property, View subtree text | present | works | - |  |
| Context menu | Open File, Preprocess | present | works | - |  |
| Context menu | Search submenu (subtree, this node, in this node, exclude, time span, project.assets.json) | present | works | - |  |
| Context menu | Copy, Copy subtree, Copy visible subtree, Copy file path, Copy children, Copy name, Copy value | present | works (clipboard checked) | - |  |
| Context menu | Show in Explorer | present | N/A: shows a file in the OS shell | - | hidden |
| Context menu | Show time and duration | present | works | - |  |
| Context menu | Go to submenu (Timeline, Tracing) | partial | works | S | Avalonia has a flat 'Go to Timeline'; add Tracing, make it a submenu like WPF |
| Context menu | Sort children by name/duration, Filter children (Ctrl+F), Hide | present | works | - |  |
| Context menu | Target graph, Property graph, NuGet graph, 'View in target graph' | missing | works once ported | S | hooks into the graph tabs |
| Context menu | Run / Debug a task | missing | N/A: runs MSBuild tasks with TaskRunner | - | desktop-only, needs TaskRunner |
| Context menu | Files tab menu: Copy, Copy All, Copy file paths, subtrees | present | works | - |  |
| Context menu | Shared results menu (Favorites, Copy, subtrees) | present | works | - |  |
| Keyboard | Tree: Enter/Space navigate, Delete hide, Esc, Ctrl+C, Ctrl+F filter, letter type-ahead | present | works | - |  |
| Keyboard | Search box: Enter, Esc, history dropdown | present | works | - |  |
| Search | Search syntax ($task, under(), start>, $time, ...) | present | works (shared core) | - |  |
| Search | Recent searches per category | present | works (localStorage) | - |  |
| Search | Mark results in tree, max results | present | works | - | settings |
| Settings | Dark theme | present | works (localStorage) | - |  |
| Settings | Other settings (virtualization, mark results, ignore embedded files, config/platform) | present | works (localStorage) | S | no UI for all of them in either viewer |
| Text viewer | AvaloniaEdit viewer, folding, highlighting, search panel, word wrap, line numbers | present | works | - |  |
| Text viewer | Save, Copy Path | present | works (download, clipboard checked) | - |  |
| Text viewer | Open in external editor | present | N/A: starts a program | - | hidden |
| Text viewer | Go to project / property (folding menu), import hyperlinks | present | works | - |  |
| Text viewer | Condition analyzer on skipped targets/tasks | present | works | - |  |
| Other | Search project.assets.json | present | works | - |  |
| Other | Secrets search ($secret) | present | works | - |  |
| Other | Preprocess with imports inlined | present | works | - |  |
| Other | Icons and node templates | present | works | - |  |

## Plan (order)
1. Context-menu and copy parity: Go to submenu, graph and tracing entries (as the views land).
2. Timeline (done), save/reload/recent, statistics checks in the browser.
3. Tracing, then the graph views (project references, targets, NuGet, properties).
4. Redact secrets in the browser; the long tail (Open Graph, attach binlog, favorites persistence).
