# 墓碑直达上次关卡（LevelQuickJump）

点主界面墓碑（冒险/挑战/生存/解谜/小游戏/我是僵尸/杂交乐园）时，
**直接进入上次打开的那个小类（章节）的关卡选择页**，不用再重新点一次小类。

| 项 | 值 |
|---|---|
| Mod ID | `levelquickjump` |
| 版本 | 1.1.0 |
| 入口类型 | `LevelQuickJumpEntry` |
| 程序集身份名 | `JTYLevelQuickJump`（包内文件名仍是 `Runtime/ModAssembly.dll`） |
| 成品 | [`dist/LevelQuickJump.pmod`](dist/LevelQuickJump.pmod) |
| 类型 | 纯托管插件（无资源覆盖、无 `provides`/`overrides`） |

---

## ★ 内部开关：点墓碑之后停在哪一层（v1.3.0 定稿）

源码顶部有一个开关（`LevelQuickJumpEntry.cs`）：

```csharp
/// true  = 停在【章节选择页】+ 把记录章节自动滚到正中（放大高亮）  ← 当前状态，已验收
/// false = 直接进【上次那个章节的关卡列表】（跳过章节页）
private static readonly bool JumpToMainCategoryPage = true;
```

| 开关 | 行为 | 实现要点 |
|---|---|---|
| **`true`（当前，已验收）** | 停在**章节选择页**，记录的那章滚到**正中**（居中项自动放大 1.5× + 高亮，两旁缩小变淡），直接点它进关卡 | ① 每帧把 `Global.currentChapterId` **钉在 -1** ⇒ `LevelChoose._Ready()` 里"非 -1 就自动展开成关卡列表"的分支不成立，于是停在章节页；② **绝不调 `LevelChoose.Select()`**（它在 Chapter 态下会 `currentMode = Level` 切页）；③ 只调 `chapterMenu.SetPos(记录章节)`，并**连续重试 12 帧**压住 `DragMenu.SetChildPos` 把新子项摆到最右端的竞争；④ 等 `chapterMenu` 子项数**连续两帧不变**（= `InitChapter()` 收尾）才开始定位 |
| `false` | 跳过章节页，直接进**上次那个章节的关卡列表** | `ButtonDown` 预置 `chapterMenu` 章节号 ⇒ 游戏 `_Ready()` 自己 `Select(currentChapterId)`；`SelectChapter()` 兜底 |

* **两种模式共用同一份记忆**（`ModLastChapter|<大类>` 一直在记录），随时翻开关、重新编译打包即可切换，**不会丢记忆**。
* 切换方式：改那一行 → `python mods\LevelQuickJump\build_and_install.py --install` → 重启游戏。

### ⚠️ 关键坑：`Select()` ≠ "选中章节"

`LevelChoose.Select(int id)` 在 `currentMode == Chapter` 时干的是**切页**：

```csharp
currentChapterIndex = id;
Global.Instance.currentChapterId = id;
GameSaveManager.Instance.SetKeyValue("AdventureChapterIndex", id);
chapterMenu.Set("alive", false);
tween.TweenProperty(camera, "global_position:y", levelChooseMarker.GlobalPosition.Y, 0.5);
currentMode = LevelChooseMode.Level;   // ★ 切到关卡页
InitLevel(id);                          // ★ 渲染关卡列表
```

所以"想在章节页上预选某章"**只能调 `chapterMenu.SetPos()`**，一调 `Select()` 就被推进关卡页
（这正是开发过程中"怎么显示的是关卡选择页面"的根因）。

### 诊断日志开关

```csharp
private static readonly bool EnableDiagLog = false;   // ← 已关闭（定稿）
private static readonly bool EnableInfoLog  = false;
```

`EnableDiagLog = false` ⇒ 自检 / 墓碑按钮扫描 / 记住小类 / 墓碑按下 / 自动定位 **全部静默**
（`Diag()` 直接 return）。排查时改成 `true` 重新构建即可，诊断代码与字符串都还在 DLL 里。
`Warn()`（`GD.PrintErr`）**不受**此开关影响 —— 真正的异常报告始终保留。

> ⚠️⚠️ **`Diag()` 方法体内必须是 `GD.Print`，绝不能写成 `Diag`。**
> 本项目踩过：用脚本批量把 `GD.Print(LogPrefix + …)` → `Diag(…)` 时，
> 连 `Diag` 自己那行也被换掉 ⇒ **无限递归 ⇒ `StackOverflowException`**
> （栈溢出连 `catch` 都抓不住，直接终止线程）⇒ 每帧回调整体失效。

### 墓碑按钮怎么找（第五次实机定案）

```csharp
// 键 = 精确节点路径（相对 MainMenu 根），与 MainMenu.cs:104-112 游戏自己用的完全一致
{ "Background/MenuTexture/AdventureButton",        "Adventure" },
{ "Background/MenuTexture/ChallengeButton",        "Challenge" },
{ "Background/MenuTexture/SurvivalButton",         "Survival" },
{ "Background/MoreBackground/PuzzleGameButton",    "Puzzle" },
{ "Background/MoreBackground/MiniGameButton",      "MiniGames" },
{ "Background/MoreBackground/IZM2GameButton",      "IZM2" },
{ "Background/MoreBackground/HybridParkGameButton","HybridPark" },
```

查找走 `FindByPathOrTail()`：① `root.GetNodeOrNull(path)` →
② 对 root 每个一级子节点再试 `child.GetNodeOrNull(path)`
（**实际命中这条**：场景树是 `SceneTree.Root → MainMenu → Background/...`）→
③ 兜底按叶子名递归。
⚠️ 递归那条比较名字**必须**写 `node.Name.ToString() == name` ——
`Node.Name` 是 `StringName`，直接与 `string` 比在实机上不生效
（这是日志恒显示 `找到=0/7` 的原因之一）。

### 「更多模式」的多级菜单 —— 本 Mod 天然覆盖

主界面右上角「更多」点开后**不是进入新场景**，而是把相机平移到右侧，
露出同一场景里 `Background/MoreBackground` 下的一排墓碑：

```
MainMenu
└── Background
    ├── MenuTexture/{AdventureButton, ChallengeButton, SurvivalButton}
    └── MoreBackground/{MiniGameButton, PuzzleGameButton, IZM2GameButton,
                        HybridParkGameButton, BattleGameButton, StarsExchangeButton}
```

这 7 个都在**同一棵节点树**里（`MainMenu.cs:104-112` 手工 `Connect` 它们），
所以本 Mod 的"递归按节点名找 + 挂 `button_down`"**对多层菜单天然生效，不需要按层级特判**；
大类名也是从按钮自己的 `pressed` 信号注册名反推的（`AdventureButtonPressed` → `Adventure`），
不依赖它在第几层。

⚠️ 唯一不覆盖的是**弹窗式**入口（如 `BattleGameButton` 开的是联机大厅对话框、
`StarsExchangeButton` 开的是兑换对话框）—— 它们本来就不进关卡选择，无需处理。

---

> **v1.0.1 修复**：用户实测「打开任意关卡开始游戏 → 退出到主菜单 → 重新点墓碑，还是跟原来一样」。
> 读 `logs\godot.log` 拿到铁证 —— `[LevelQuickJump] 反射成员缺失：… SaveMgr=False …`，
> 于是 `EnsureReflection()` 恒失败、**每次调用开头就 return，整包逻辑一行都没执行**。
> 真因有三个，都已修：
>
> 1. ★★★ **`GameSaveManager.Instance` 是「字段」不是「属性」**（最终真因，2026-10-01 实机定案）
>
>    ```csharp
>    // Core/GameSaveManager/GameSaveManager.cs:102
>    public static GameSaveManager Instance;                        // ← 字段！
>    // 同一套代码里另外两个却是属性：
>    //   Core/Global/Global.cs:25
>    //       public static Global Instance { get; private set; }
>    //   Core/TowerDefenseManager/TowerDefenseManager.cs:211
>    //       public static TowerDefenseManager Instance { get; private set; }
>    ```
>
>    原来只用 `GetProperty("Instance", Public|Static)` ⇒ 对 `GameSaveManager` 恒为 `null`
>    ⇒ `_refFailed = true` ⇒ 功能全灭。**字段/属性混用是这套代码的坑**
>    （另外两个 Mod 之所以没事，正因为它们读的是属性）。
>    ⇒ 现在**先试字段、再试属性**（统一走 `SaveMgr()`）。
> 2. **`GetKeyValue`/`SetKeyValue` 通道不能用于自定义键** ——
>    `GameSaveManager.GetCategoryValue()`（`GameSaveManager.cs:762`）对游戏不认识的键会执行
>    `categoryDictionary[key] = initData[key]`，而 `initData`（`KEY_INIT_DICT`）里没有我们的键
>    ⇒ **抛 `KeyNotFoundException`**（Godot 的 `Dictionary` 索引器没有 `TryGetValue`），被 catch 吞掉。
>    **写是好的、读是坏的**（写侧因键已存在不会走到那行），最难查的不对称。
>    ⇒ 改用 `GetKeyDictionary()` 取原始字典自行读写（全程 `ContainsKey` 守卫）+ 显式 `Save()`。
> 3. **只写 `Global.currentChapterId` 不够** —— `LevelChoose._Ready()` 读的是它执行那一瞬的值，
>    主菜单清成 -1 后 `_Ready()` 已跑完，我们再写回去它不会重新 `Select()`。
>    ⇒ 改为**直接调用 `LevelChoose.Select(id)`**（`LevelChoose.cs:286`，public），完全不依赖时序。
>
> 附带修掉一个逻辑错：原来"每个大类只恢复一次"的作用域是**整个游戏进程**，
> 于是第一次进关卡选择就把标记用掉了、第二次进来被自己挡掉。现在改成
> **每次进入关卡选择允许恢复一次**（离开场景即清零）。

---

## 1. 需求与问题根因

**需求**：从主界面（墓碑界面）进对应关卡时，**每次都要重新选择大类和小类**；
希望点墓碑就直接跳到"最近打开的小类关卡选择页面"。

### 名词对齐

| 玩家说法 | 代码里的东西 |
|---|---|
| **大类** | `Global.Instance.currentLevelChoose` = `"Adventure"` / `"Challenge"` / `"Survival"` / `"Puzzle"` / `"MiniGames"` / `"IZM2"` / `"HybridPark"` |
| **小类（章节）** | `LevelChoose.currentChapterIndex`，并镜像到 `Global.Instance.currentChapterId` |

### 根因（`Scene/LevelChoose/LevelChoose.cs` + `Scene/MainMenu/MainMenu.cs`）

游戏**本来就会**自动展开小类 —— `LevelChoose._Ready()` 末尾：

```csharp
else if (!IsModBrowser && Global.Instance.currentChapterId != -1
         && Global.Instance.enterLevelMode == "LevelChoose")
{
    Select(Global.Instance.currentChapterId);      // ★ 直接展开那个小类
}
```

**但主菜单每个墓碑回调都先把记忆抹掉**：

```csharp
public void AdventureButtonPressed()
{
    if (!wait)
    {
        Global.Instance.currentLevelChoose = "Adventure";
        Global.Instance.currentChapterId = -1;      // ★ 记忆被清，所以才要重点一次
        Global.Instance.currentLevelId = -1;
        SceneManager.Instance.ChangeScene("LevelChoose");
        ...
```

⇒ 进场景时 `currentChapterId == -1`，`_Ready()` 的条件不成立，于是停在**大类选择**。
7 个墓碑回调（`Adventure/Challenge/Survival/PuzzleGame/MiniGame/IZM2Game/HybridParkGame`）
全是这个形态。

---

## 2. 修法（纯运行时，不改任何游戏资源/场景）

* **记录**：在关卡选择场景里，只要 `currentChapterId >= 0` 就按大类存起来：

  ```
  GameSaveManager.SetKeyValue("ModLastChapter|<大类>", <章节号>)
  ```

  写进**游戏存档** ⇒ 重启游戏依然有效。只在值变化时落盘，不每帧写。

* **恢复**（两条通道，一起才稳）：

  | 通道 | 时机 | 作用 |
  |---|---|---|
  | ⓪ `ButtonDown` 钩子 | 墓碑**按下**那一瞬（早于 `Pressed`） | 抢"同一帧的先后"：先把记忆写回 `currentChapterId` |
  | ① 每帧兜底 | `SceneTree.process_frame` | 抢"场景切换那几帧"：发现"在 LevelChoose 且 `currentChapterId == -1`"就写回 |

  `SceneManager.ChangeScene` 是 `async void`（加载 → `ChangeSceneToPacked`），
  这中间隔着若干帧；`process_frame` 每帧都在跑，所以在 `LevelChoose._Ready()` 读到它之前
  就已经写回去了。两条通道冗余覆盖，任一条生效即可。

* **大类名不写死**：优先从按钮自身的 `pressed` 信号连接里读注册名
  （Godot 的 `Connect("pressed", callable)` 会把方法名注册进来，实测就是
  `"Adventure"`/`"Challenge"`/…，与 `currentLevelChoose` 同源），
  取不到才回落到内置的 7 项对照表。

### 防"弹回"（不会跟玩家抢控制权）

* 只在玩家**仍停在大类选择态**（`LevelChoose.currentMode == Chapter(0)`）时才恢复；
* 同一 (大类, 章节) 在一次游戏进程里**只恢复一次** —— 玩家按「返回」回到大类选择时
  不会被立刻弹回小类；
* 记忆的章节号超出当前大类的章节数时跳过，不硬跳。

---

## 3. 硬约束遵守情况

| 约束 | 状态 |
|---|---|
| `runtimeAssembly` 必须是字面量 `Runtime/ModAssembly.dll` | ✅ |
| `runtimeApiVersion` 必须恰好 `1` | ✅ |
| `Runtime/` 下只许一个 `ModAssembly.dll` | ✅（打包护栏 1） |
| 包内不得出现可执行文件（`.cs`/`.gd`/`.exe`…） | ✅（`verify_pmod.py` 通过） |
| `<AssemblyName>` 用本 Mod 唯一名 | ✅ `JTYLevelQuickJump` |
| 入口三个回调绝不抛 | ✅ 全部 try/catch |
| 不用自定义 Node 的 `_Process` | ✅ 走 `SceneManager` 之外的 `process_frame` 信号 |
| 反射调用签名核对 | ✅ `GameSaveManager.GetKeyDictionary() : Dictionary` / `Save()`（源码 `GameSaveManager.cs:747/399`）；⚠️ 注意 `GameSaveManager.Instance` 是**字段**（`:102`），不是属性 |

---

## 4. 目录结构

```
LevelQuickJump/
├── mod.json                       包清单
├── build_and_install.py           编译 → 拷贝到 Runtime/ → 打包 → 装机
├── build_pmod.py                  单打包
├── runtime_src/
│   ├── LevelQuickJump.csproj
│   ├── LevelQuickJumpEntry.cs     ★ 全部逻辑（约 18 KB）
│   └── bin/Release/JTYLevelQuickJump.dll
├── Runtime/ModAssembly.dll
└── dist/LevelQuickJump.pmod       ★ 成品
```

重新构建：

```powershell
python mods\LevelQuickJump\build_and_install.py --install
```

存档键（想重置记忆时，删掉这些键即可）：

```
ModLastChapter|Adventure
ModLastChapter|Challenge
ModLastChapter|Survival
ModLastChapter|Puzzle
ModLastChapter|MiniGames
ModLastChapter|IZM2
ModLastChapter|HybridPark
```

---

## 5. 怎么看它有没有生效（**不用猜，看日志**）

日志文件（本机实测路径）：

```
%APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\logs\godot.log
```

搜 `[LevelQuickJump]`。**下面这些行全部直出**（`GD.Print`，不受日志总开关门控）：
启动自检一次，之后 `记住小类` / `墓碑按下` / `恢复小类` 各在发生时输出。

```
[LevelQuickJump] 自检：Global.Instance=True currentChapterId=True currentLevelChoose=True
                 enterLevelMode=True GameSaveManager.Instance=True GetKeyDictionary=True
                 Save=True GetSignalConnectionList=True → 全部命中，功能可用
[LevelQuickJump] 存档键前缀=ModLastChapter|；登记的墓碑按钮 7 个：AdventureButton,ChallengeButton,…
[LevelQuickJump] 记住小类：大类=Adventure 章节=2（已写入存档键 ModLastChapter|Adventure，回读=2）
[LevelQuickJump] 墓碑按下：Adventure → 预置小类 2
[LevelQuickJump] 恢复小类：大类=Adventure 章节=2（主菜单原本清成了 -1，已写回并直接 Select）
```

**自证顺序**（一轮就能判定）：

| 你做什么 | 该看到哪条 |
|---|---|
| 启动游戏 | `自检：… → 全部命中，功能可用`；否则看哪一项是 `False` |
| 进某个大类、点一个小类 | `记住小类：…（…回读=N）` ← **`回读=N` 必须等于你点的那个章节号**，这是读写通道好的证据 |
| 退回主菜单，再点同一个墓碑 | `墓碑按下：… → 预置小类 N`，随后 `恢复小类：…` |

| 日志现象 | 含义 |
|---|---|
| 一行都没有 | Mod 没被加载 → 查 `Mods\enabled_mods.json` 里有没有 `levelquickjump` |
| 自检里 `GetKeyDictionary=False` 或 `Save=False` | 反射没拿到存档通道 → 把这行发我 |
| 有「记住小类」但 `回读=-1` | 存档字典写入没生效（本轮修的正是这条链路） |
| 有「记住小类」但退回后**没有**「墓碑按下」 | 按钮没挂上 / 大类名没推出来 → 把「登记的墓碑按钮」那行发我 |
| 「墓碑按下」有、但没有「恢复小类」 | `IsInChapterMode()` 或越界检查挡掉了 |

---

## 6. 未验证项（需要实机确认）

1. **首次使用**：装好后第一次点某个墓碑时**没有记忆**，仍会正常显示大类选择
   （这是预期行为）；进一次小类后再回主界面重点同一个墓碑，应直接进入该小类；
2. 7 个墓碑（含"更多"里的解谜/小游戏/我是僵尸/杂交乐园）逐个确认；
3. **重启游戏**后记忆是否仍在（验证写的是存档而不是内存）；
4. 「返回」回到大类选择后**不被弹回**小类；
5. 某大类换了关卡包（章节数变少）时不会跳到越界章节。

> 说明：本轮**无法**在开发侧完成实机验证——headless 启动会在联网版本检查
> （`https://api.pvzhe.com/new_version`）处停住，到不了加载 Mod 的 `Loading` 场景。
> 上面第 5 节的日志判据就是为此准备的：**你一跑游戏就能自证**。
>
> 已离线核对过的（静态证据）：
> * 7 个墓碑按钮名全部存在于 `Scene/MainMenu/MainMenu.tscn`（实测清单比对）；
> * `GameSaveManager.GetKeyDictionary() : Dictionary`（`GameSaveManager.cs:747`）与
>   `Save()`（`:399`）签名一致；**`GameSaveManager.Instance` 是字段**（`:102`）——
>   与 `Global.Instance`（`Global.cs:25`，属性）、`TowerDefenseManager.Instance`
>   （`:211`，属性）**形态不同**，代码里字段/属性都试；
> * `Global.currentChapterId` / `currentLevelChoose` 与 `Global.cs:105/107` 一致；
> * `LevelChoose.currentMode` / `currentChapterList` 与 `LevelChoose.cs:65/59` 一致；
> * `LevelChoose.Select(int)` public，见 `LevelChoose.cs:286`。

