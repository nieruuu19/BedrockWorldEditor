# 🌍 Bedrock World Editor

A fast, lock-free **Minecraft: Bedrock Edition** NBT & LevelDB world editor for Windows, built with WPF (.NET) and modeled after the classic **Universal Minecraft Editor (UME)** interface and workflow.

---

## ✨ Features

### 🎨 Authentic UME Interface & Workflow
* **Scroll-Synced Zebra Striping:** Crisp 22px alternating white/grey rows across the Worlds list, Players list, and NBT TreeView.
* **3-State Row Selection:** Authentic UME hover, active-click deep sky-blue, and multi-select light-blue states with full-row highlights.
* **Custom Vector Badges & Icons:** Folded-document primitive badges (`B`, `S`, `I`, `L`, `F`, `D`), two-tone `01` array badges (`ByteArray` & `IntArray`), serif `TT` string icons (`T` + `t`), 3-dot bulleted `List` icons, 3D `Compound` package icons, and 8×8 pixel-accurate Steve player icons.
* **UME Tag-Type Ordering:** Automatically groups and sorts `NbtCompound` children by UME priority (`Compound` → `List` → `Byte` → `Short` → `Int` → `Long` → `Float` → `Double` → `ByteArray` → `IntArray` → `String`), then alphabetically (`A–Z`), dynamically re-sorting when tags are renamed.

### 🔓 Lock-Free LevelDB & `level.dat` Session Engine
* **Zero Persistent File Locks:** Opens Bedrock's `db/` LevelDB database and `level.dat` for just a few milliseconds to cache entries into memory, then immediately releases all OS file handles.
* **Seamless Multi-Target Switching:** Edit multiple players (`~local_player`, `player_server_*`) and `World Settings (level.dat)` in the same session without nagging popups. Each target remembers its own expanded tree branches, selection, scroll position, and 30-step `Ctrl+Z` undo history.
* **Batch Save:** Commits all modified players, staged player deletions, and `level.dat` changes in a single batch when you click **Save World** (`Ctrl+S`)

### 🛠️ Full NBT Editing & Creation Suite
* **Interactive Top Toolbar:** Click any tag icon on the top bar to insert a new tag directly into the active container with inline naming (`[TextBox] [OK]`).
* **Built-In 3-Column Hex Editor:** Creating or double-clicking a `ByteArray` or `IntArray` tag opens a UME-style **Hex Editor** featuring an 8-digit hex address offset column (`00000000`), hex byte grid, live ASCII preview, and tag rename bar.
* **Inline Value Editing & Tag Renaming:**
  * **Double-Click Value** (or Right-Click → **Edit Value**): Edit primitive values inline with automatic type validation.
  * **Double-Click Tag Name/Badge** (or press `F2` / Right-Click → **Rename**): Rename tags in-place with automatic alphabetical re-sorting.
* **Live Coordinate & Compass Previews:**
  * `Pos` lists automatically display `(X: 0.0, Y: 0.0, Z: 0.0)` summaries.
  * `Rotation` lists automatically compute compass and pitch summaries, e.g., `(Facing NW, Looking Straight)`.
* **Multi-Selection & Cross-World Clipboard:** Supports `Ctrl+Click`, `Shift+Click`, click-and-drag sweep selection, `Ctrl+A` (Select All in container), `Ctrl+C` (Copy), `Ctrl+X` (Cut), `Ctrl+V` (Paste across players or worlds), `Del` (Delete), and `Move Up` / `Move Down` inside lists.

### 👤 Automatic Account & Gamertag Discovery
* Scans `%AppData%\Minecraft Bedrock\Users` automatically on startup.
* Resolves local XUIDs and Gamertags from `options.txt` and caches online Xbox Live Gamertag lookups locally in `gamertag_cache.json`.

---

## ⌨️ Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| **`Ctrl + S`** | Save all modified players and `level.dat` changes to the world |
| **`Ctrl + Z`** | Undo last change in the active file (preserves exact scroll position) |
| **`F2`** | Rename selected tag inline |
| **`Double-Click`** | Edit tag value inline (or open **Hex Editor** for `ByteArray` / `IntArray`) |
| **`Ctrl + C`** | Copy selected tag(s) to the NBT clipboard |
| **`Ctrl + X`** | Cut selected tag(s) |
| **`Ctrl + V`** | Paste tag(s) into the selected `Compound` or `List` |
| **`Ctrl + A`** | Select all sibling tags in the current container |
| **`Delete`** | Delete selected NBT tag(s) or selected player(s) |
| **`Enter` / `Esc`** | Commit or cancel active inline edit |

---

## 🚀 Getting Started

### Prerequisites
* **OS:** Windows 10 / 11 (x64)
* **SDK:** .NET 6.0 / 8.0 SDK (Windows Desktop / WPF)
* **Dependencies:**
  * [`MiNET.fnbt`](https://www.nuget.org/packages/MiNET.fnbt) (NuGet package for NBT serialization)
  * Native **`leveldb.dll`** and **`zlib.dll`** (for Bedrock's Zlib-raw LevelDB compression) placed in the application output directory (`bin/x64/Debug` or `bin/x64/Release`).
  * **OpenXBL API Key (Optional):** Used for online XUID-to-Gamertag resolution. You can generate a free API key at `https://xbl.io/`.

### Building & Running
1. Clone the repository:
   ```bash
   git clone https://github.com/nieruuu19/BedrockWorldEditor.git
2. Open `BedrockWorldEditor.sln` in **Visual Studio 2022+**.
3. Set the solution platform to **`x64`**.
4. **(Optional) Configure OpenXBL API Key:** Open `MainWindow.xaml.cs` and replace `XblApiKey` with your own OpenXBL key:
```csharp
private const string XblApiKey = "YOUR_OPENXBL_API_KEY_HERE";

```


*(If left blank or restricted, the editor will automatically fall back to offline XUID/Gamertag detection via `options.txt` and `gamertag_cache.json`.)*
5. Ensure both **`leveldb.dll`** and **`zlib.dll`** are present in your project and set to **`Copy if newer`** (or placed directly in your build output folder).
6. Press **`F5`** (or `Ctrl + Shift + B`) to build and run.

---

## ⚠️ Usage Note

While **Bedrock World Editor** uses a lock-free memory session so it never locks your world folder while browsing or editing, **Minecraft: Bedrock Edition** itself locks `db/` while you are actively loaded into a world. Always **Save & Quit to Title** in Minecraft before clicking **Save World** in the editor.
