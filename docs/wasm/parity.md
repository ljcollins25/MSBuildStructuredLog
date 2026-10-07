# WPF viewer feature parity (shared Avalonia UI and browser)

Reference: src/StructuredLogViewer (WPF). Everything is built in StructuredLogViewer.Avalonia.Core, so the desktop Avalonia
head and the browser share it. Source of the list: a name-by-name diff of MainWindow, BuildControl, SearchAndResultsControl,
TextViewerControl and DocumentWell between the WPF and Avalonia code, plus the WPF XAML (menus, tabs, named elements).
The Avalonia column is the status of the shared UI; the browser column says works, or N/A with the reason. Effort: S hours, M a day, L several days.
"to verify" means the code exists but is not yet exercised in the browser.

Counts (Avalonia): present 63, partial 0, missing 3, N/A 1  (total 67)

| Area | Feature | Avalonia | Browser | Effort | Notes |
|---|---|---|---|---|---|
| Main window | File > Open Log (Ctrl+O) | present | works (file picker, drag and drop, ?url=) | - |  |
| Main window | File > Open Graph (.dgml/.graph) | present | left out of the menu: graph files open by drop; no separate dialog | - | GraphHostControl.FromFile |
| Main window | File > Reload (F5) | present | works for a log opened from a URL (File menu, shown only then): downloads it again. Hidden for a picked or dropped file: the browser keeps no path to re-read | - | BrowserShell.lastUrl; e2e checks the menu for both |
| Main window | File > Save Log As (Ctrl+S) | present | works: File menu, downloads the opened bytes unchanged (original file name); e2e checks name and size | - | BrowserShell.SaveLogAs, interop.js downloadBytes. Ctrl+S not bound (the browser's own save-page) |
| Main window | File > Redact Secrets (Ctrl+R) | present | N/A for now: rewrites the log through a file save dialog and re-opens it; left out of the browser menu until a download-and-reopen flow is designed | S | RedactInputControl exists |
| Main window | File > Statistics | present | works: File menu (binlogs only), adds the Statistics node to the tree; e2e checks it | - | BinlogStats reads a file, so the bytes are written to the in-memory file system for the call |
| Main window | Recent Logs / Recent Projects, Clear | present | not in the browser menu: recent logs hold names only and cannot be reopened (settings still stored) | S | paths cannot be reopened in a browser |
| Main window | Start Page, welcome screen | present | works: shared WelcomeScreen; Open Project/Solution hidden (needs MSBuild) | - | WelcomeScreen.ShowOpenProject/ShowOpenFromUrl |
| Main window | Open from URL (start page box and ?url=) | present | works: HTTP Range when the server supports it (else one download); CORS, HTML and non-binlog responses give a clear message on the start page | - | browser-only; ILogSource/ILogSourceProvider (LogSource.cs) is the plug-in point for the paged reader; e2e covers CORS, no-Range, 404, HTML |
| Main window | Enable tree virtualization (setting) | missing | missing: Avalonia TreeView cannot virtualize; every expanded node is a live control, so a node with ~50k children is slow and memory-hungry | L | App.xaml comment: only the root level virtualizes and it breaks AutoScrollToSelectedItem (AvaloniaUI/Avalonia#10985). Evaluated TreeDataGrid, see the section below: not adoptable (licence); a flat virtualized list is the licence-free route |
| Main window | Build / Rebuild Solution/Project (F6, Shift+F6) | present | N/A: runs MSBuild, a browser cannot start processes | - | to hide |
| Main window | Set MSBuild path | present | N/A: no MSBuild in a browser | - | to hide |
| Main window | Help: Search Syntax, links | present | works: Help menu with Search Syntax and the two project links, each opens in a new tab. About not added | - | JsInterop.OpenUrl |
| Main window | Exit (Alt+F4) | present | N/A: no window to close | - | to hide |
| Main window | Open in VS Code, VS Code variant dropdown, hint bar | missing | N/A: starts a local program | - | desktop-only; port the buttons to the Avalonia head later |
| Main window | Attach binlog (multi-log) button | present | N/A: it only adds binlogs to the VS Code hand-off, which a browser cannot start | - | BuildControl.AttachBinlog stays for the desktop head |
| Main window | Exception panel (shows and copies error text) | present | works | - |  |
| Main window | Ctrl+mouse wheel zoom of the whole UI | present | works | - |  |
| Main window | Ctrl+F / Ctrl+Shift+F global search shortcuts | present | works (BrowserShell, TopLevel key handler; e2e checks Ctrl+Shift+F) | - |  |
| Main window | Ctrl+C copy, Ctrl+0 reset zoom | present | Ctrl+C copies the tree selection (as in the tree row below); the desktop window's own Ctrl+C only copies the build command line (N/A). Ctrl+0 / Ctrl+wheel are the browser's native page zoom | - |  |
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
| Tabs | Tracing (zoom, scroll, selection) | present | works | - | ported: single-surface renderer, heat graph, ruler, drag pan, Ctrl+wheel/slider zoom, click/double-click, P2P lines, display toggles menu. Touch gestures not ported (pointer events cover touch taps) |
| Tabs | Project References graph | present | works | - | ported from WPF GraphControl/GraphHostControl (layered text blocks, edges on select, path highlight, filter modes, locate, Text/Vertices). Copy screenshot not ported |
| Tabs | Targets graph | present | works | - | ported from WPF GraphControl/GraphHostControl (layered text blocks, edges on select, path highlight, filter modes, locate, Text/Vertices). Copy screenshot not ported |
| Tabs | NuGet graph | present | works | - | ported from WPF GraphControl/GraphHostControl (layered text blocks, edges on select, path highlight, filter modes, locate, Text/Vertices). Copy screenshot not ported |
| Tabs | Properties graph | present | works | - | ported from WPF GraphControl/GraphHostControl (layered text blocks, edges on select, path highlight, filter modes, locate, Text/Vertices). Copy screenshot not ported |
| Tabs | Breadcrumb bar, project context bar | present | works | - |  |
| Tabs | Document well: source tabs, close, context | present | works | - |  |
| Context menu | Add/Remove Favorites | present | works | - |  |
| Context menu | View source, View full text, View property, View subtree text | present | works | - |  |
| Context menu | Open File, Preprocess | present | works | - |  |
| Context menu | Search submenu (subtree, this node, in this node, exclude, time span, project.assets.json) | present | works | - |  |
| Context menu | Copy, Copy subtree, Copy visible subtree, Copy file path, Copy children, Copy name, Copy value | present | works (clipboard checked) | - |  |
| Context menu | Show in Explorer | present | N/A: shows a file in the OS shell | - | hidden |
| Context menu | Show time and duration | present | works | - |  |
| Context menu | Go to submenu (Timeline, Tracing; Target graph pending the graph views) | present | works | - | |
| Context menu | Sort children by name/duration, Filter children (Ctrl+F), Hide | present | works | - |  |
| Context menu | Target graph, Property graph, NuGet graph, 'View in target graph' | present | works | - | Target/Property/NuGet graph and Go to > Target graph |
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

## Tree virtualization and TreeDataGrid (evaluated)
- Gap: WPF has "Enable tree virtualization"; Avalonia's TreeView cannot virtualize expanded descendants (only the root level; it also breaks AutoScrollToSelectedItem, AvaloniaUI/Avalonia#10985). Every expanded node is a live control, so a node with tens of thousands of children is slow and uses a lot of memory.
- TreeDataGrid: this project is on Avalonia 12.0.1. The package line for Avalonia 12 (Avalonia.Controls.TreeDataGrid 12.x, .NET 8+, depends on AvaloniaUI.Licensing) needs a paid Avalonia Accelerate licence since 11.2.0; the last MIT release is the 11.1.x line, which does not run on Avalonia 12. The open-source repository was archived in October 2025. So it is not adoptable without a licence decision, and it was not prototyped.
- Scope even with a licence: the main tree is TreeView-based in BuildControl (about 80 treeView references, TreeViewItem styles for expand/select/visibility, SelectedTreeViewItem / TreeContainerFromItem, right-click selection, keyboard handlers, per-node templates in App.xaml, the search-result dot). TreeDataGrid wants one row model and a template column with an expander; roughly 1-2 weeks including the e2e and measurements.
- Licence-free option: a flat, virtualized list (ListBox/ItemsControl with VirtualizingStackPanel) over the currently visible rows, with indentation, an expander glyph, and ScrollIntoView for scroll-to-result. Same size of work, no new dependency.

## Plan (order)
1. Context-menu and copy parity: Go to submenu, graph and tracing entries (as the views land).
2. Timeline (done), save/reload/recent, statistics checks in the browser.
3. Tracing, then the graph views (project references, targets, NuGet, properties).
4. Redact secrets in the browser; the long tail (Open Graph, attach binlog, favorites persistence).
