using System;
using System.Reflection;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「墓碑直达上次关卡」Mod 的托管运行时入口。
///
/// ── 需求（用户）────────────────────────────────────────────────
/// 从主界面（墓碑界面）点某个大类（冒险/挑战/生存/解谜/小游戏/我是僵尸…）进去后，
/// **每次都要重新点一次大类里的小类（章节）**，很烦。
/// 要求：**点墓碑时直接跳到"上次打开的那个小类"的关卡选择页**。
///
/// ── 机制（解包源码定案）────────────────────────────────────────
/// 1. 大类 = `Global.Instance.currentLevelChoose`（"Adventure"/"Challenge"/"Survival"/
///    "Puzzle"/"MiniGames"/"IZM2"/"HybridPark"…）。
/// 2. 小类 = `LevelChoose.currentChapterIndex`，并镜像到
///    `Global.Instance.currentChapterId`（`LevelChoose.Select()` 里赋值）。
/// 3. **游戏本来是记得的**：`LevelChoose._Ready()` 末尾有
///    ```csharp
///    else if (!IsModBrowser && Global.Instance.currentChapterId != -1
///             && Global.Instance.enterLevelMode == "LevelChoose")
///        Select(Global.Instance.currentChapterId);      // ← 自动进小类
///    ```
///    ⇒ 只要 `currentChapterId != -1`，进场景就会**直接展开那个小类**。
/// 4. **但主菜单每个墓碑回调都把它清成 -1**（`MainMenu.cs`）：
///    ```csharp
///    Global.Instance.currentLevelChoose = "Adventure";
///    Global.Instance.currentChapterId = -1;      // ★ 记忆被抹掉，所以才要重点一次
///    Global.Instance.currentLevelId = -1;
///    SceneManager.Instance.ChangeScene("LevelChoose");
///    ```
///
/// ── 修法（不改游戏资源、不改场景，纯运行时）────────────────────
/// * **记录**：在关卡选择场景里，只要 `currentChapterId >= 0` 就把它按大类存起来
///   （`GameSaveManager.SetKeyValue("ModLastChapter|" + 大类, id)`，跨会话保留）。
/// * **恢复**：每帧发现"当前场景是 LevelChoose 且 `currentChapterId == -1`"
///   （= 刚从墓碑进来、记忆被抹了）就把它写回存档里的值。
///   `SceneManager.ChangeScene` 是 async 的：主菜单标 -1 → 加载 → LevelChoose._Ready()
///   读它。我们的每帧回调**在 _Ready() 之前**就已经把值写回去了（`SceneManager` 在
///   `_PhysicsProcess` 里 `ChangeSceneToPacked` 换场景，而 `process_frame` 每帧都在跑；
///   换场景那一帧与 `_Ready()` 之间没有"用户可见"的窗口），实测见交付说明。
///
/// ── 铁律 ──────────────────────────────────────────────────────
/// 三个入口回调一律不许抛；每帧逻辑走 `process_frame` 信号
/// （手写 csproj 无 Godot 源码生成器 ⇒ 自定义 Node 的 _Process 不会被调用）。
/// </summary>
public sealed class LevelQuickJumpEntry : IXWModRuntimeEntry
{
	// 赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存
	internal static string SponsorQrRoot = null;

	private const string LogPrefix = "[LevelQuickJump] ";

	/// <summary>存档键前缀：每个大类一个小类记忆。用 `|` 与游戏自带键分开。</summary>
	private const string SaveKeyPrefix = "ModLastChapter|";
		/// <summary>★ 新增：记录"上次的具体关卡 id"（与章节并列存一份）。</summary>
		private const string LevelKeyPrefix = "ModLastLevel|";
		private static bool _wantLevel;
		private static int _pendingLevelId = -1;

	/// <summary>
	/// ★★★ 内部开关（2026-10-01 按用户要求加入）：点击墓碑之后停在**哪一层**。
	///
	///   `true`（= 用户已验收的行为，2026-10-01 达成）
	///       ⇒ 墓碑点进去停在**章节选择页**，并且**自动把记录的那个章节滚到正中**
	///         （`chapterMenu.SetPos` → 居中项自动放大 1.5× + 高亮，两旁缩小变淡）。
	///         玩家看到"章节已预取"，**直接点它**就进关卡页，不用手动滑动。
	///         实现要点：
	///           · 每帧把 `Global.currentChapterId` 钉在 -1，让 `LevelChoose._Ready()`
	///             那条"非 -1 就自动展开成关卡列表"的分支不成立 ⇒ 停在章节页；
	///           · **绝不调 `Select()`** —— 它在 Chapter 态下会切页（见文件内长注释）；
	///           · 只调 `chapterMenu.SetPos(记录章节)`，并连续重试 12 帧压住
	///             `DragMenu.SetChildPos` 把新子项摆到最右端的竞争。
	///
	///   `false`（= **当前状态**，切到反状态给用户看效果）
	///       ⇒ **直接跳进上次那个章节的关卡列表**（跳过章节页）。
	///         实现：`ButtonDown` 预置 `currentChapterId` ⇒ 游戏 `_Ready()` 自己
	///         `Select(currentChapterId)`；`SelectChapter()` 兜底。
	///
	/// 两种模式**共用同一份记忆**（`ModLastChapter|<大类>` 一直在记录），
	/// 所以随时翻这个开关、重新编译打包即可切换，不会丢记忆。
	/// </summary>
	/// <summary>
		/// ★ 点击墓碑后停在**哪一层**（由「Mod 配置」的 jumpTarget 决定）：
		///   true  = 停在**章节选择页**（并把上次的章节滚到正中/高亮）
		///   false = **直达章节**（自动展开上次那个小类，直接看到关卡列表）
		/// ⚠️ 两种模式**共用同一份记忆**（`ModLastChapter|<大类>`），随时切换不会丢记录。
		/// </summary>
		private static bool JumpToMainCategoryPage = true;
		private const string KJumpTarget = "jumpTarget";

	/// <summary>诊断日志总开关（排查时置 true）。</summary>
	private static bool EnableInfoLog;   // 由 Initialize 从「Mod 配置」读取（原为编译期常量）

	/// <summary>
	/// ★ **诊断直出总开关**（2026-10-01 用户要求"关掉 log"后新增）。
	///
	/// `false` ⇒ 下面所有 `GD.Print(...)` 的诊断行**全部静默**（自检、按钮扫描、
	/// 记住小类、墓碑按下、准备恢复、恢复小类……一个都不打）。
	/// `true`  ⇒ 全部输出，用于排查。
	///
	/// ⚠️ 与 <see cref="EnableInfoLog"/> 的区别：`EnableInfoLog` 门控的是 `Info()` 通道
	///   （历史遗留，默认就关着）；本开关门控的是**我为了定位那几轮 bug 临时加的直出行**。
	///   两个都关 = 这个 Mod 在日志里完全安静（只剩 `Warn` 的异常报告，那个应该保留）。
	/// </summary>
	private static bool EnableDiagLog;   // 由 Initialize 从「Mod 配置」读取（原为编译期常量）

	/// <summary>诊断直出（受 <see cref="EnableDiagLog"/> 门控，异常全吞）。</summary>
	private void Diag(string msg)
	{
		if (!EnableDiagLog)
		{
			return;
		}
		// ⚠️⚠️ 这里**必须**是 GD.Print，不能是 Diag —— 我先前用脚本批量替换
		//   `GD.Print(LogPrefix + …)` → `Diag(…)` 时把本方法体内那行也换掉了，
		//   于是 `Diag` 自己调自己 ⇒ **无限递归 ⇒ StackOverflowException**，
		//   而且它连 catch 都抓不住（栈溢出直接终止线程）⇒ 每帧回调整体失效，
		//   表现就是"墓碑点了完全没效果"。第五次实机才揪出来。
		try { if (EnableInfoLog) { GD.Print(LogPrefix + msg); } } catch { }
	}

	/// <summary>
	/// ★ 启动自检（**始终输出**，不受 <see cref="EnableInfoLog"/> 门控）。
	///
	/// 为什么必须有它：本 Mod 全部靠反射摸游戏内部成员，而三个入口回调与每帧逻辑
	/// 一律 try/catch（铁律：绝不抛，抛了会整包回滚）。代价是**反射写错会静默失效**
	/// ——功能不生效、日志一片安静，最难查。
	/// ⇒ 启动时把每个反射目标的命中情况打一次（<c>GD.Print</c> 直出），
	///   一眼就能定位"是哪一项没取到"。
	/// </summary>
	private const bool StartupSelfCheck = true;

	private XWModRuntimeContext _context;
	private SceneTree _tree;
	private Callable _tick;
	private bool _connected;
	private bool _faultReported;

	/// <summary>
	/// 主界面「墓碑」按钮 → 它代表的大类。
	///
	/// ⚠️ **大类名不写死**：优先从按钮自身的信号连接里读注册名
	/// （Godot 的 `Connect("pressed", callable, methodName)` 会把方法名注册进来，
	///   实测就是 "Adventure"/"Challenge"/…），对应 `Global.Instance.currentLevelChoose`
	///   的那套取值；取不到才回落到这张表。
	/// </summary>
	private static readonly System.Collections.Generic.Dictionary<string, string> GraveButtons
		= new System.Collections.Generic.Dictionary<string, string>
	{
		// 键 = 精确节点路径（相对 MainMenu 根）→ 值 = 大类（兜底；实际优先从信号注册名反推）
		//
		// ★★★ 2026-10-01 第五次实机定案：**改用精确路径**。
		//   之前用「递归按节点名找」，而里面写的是 `node.Name == name`
		//   —— `Node.Name` 是 **`StringName`** 不是 `string`，这个比较从没在实机上生效过
		//   （实机日志一直 `找到=0/7`）。路径取自 `Scene/MainMenu/MainMenu.tscn` 实测，
		//   与 `MainMenu.cs:104-112` 游戏自己用的路径完全一致。
		//   注：前 3 个挂 MenuTexture，后 4 个是「更多」那一排（同一场景，相机平移到右侧露出）。
		{ "Background/MenuTexture/AdventureButton", "Adventure" },
		{ "Background/MenuTexture/ChallengeButton", "Challenge" },
		{ "Background/MenuTexture/SurvivalButton", "Survival" },
		{ "Background/MoreBackground/PuzzleGameButton", "Puzzle" },
		{ "Background/MoreBackground/MiniGameButton", "MiniGames" },
		{ "Background/MoreBackground/IZM2GameButton", "IZM2" },
		{ "Background/MoreBackground/HybridParkGameButton", "HybridPark" },
	};

	/// <summary>已挂钩 `ButtonDown` 的按钮（按实例 id），避免重复挂。</summary>
	private readonly System.Collections.Generic.HashSet<ulong> _hooked
		= new System.Collections.Generic.HashSet<ulong>();

	/// <summary>墓碑按钮 `button_down` 的回调（带大类名参数）。</summary>
	private Callable _graveDown;

	// ---- 反射缓存 ----
	private bool _refReady;
	private bool _refFailed;
	private PropertyInfo _pGlobal;
	private PropertyInfo _pChapterId;
	private PropertyInfo _pLevelChoose;
	private PropertyInfo _pEnterLevelMode;
	private PropertyInfo _pSaveMgr;
	private FieldInfo _pSaveMgrField;
	private MethodInfo _mGetKeyDict;
	private MethodInfo _mSave;
	private Type _tNode;
	private MethodInfo _mGetConnections;

	/// <summary>上一帧看到的 (大类, 小类)，只在变化时落盘，避免每帧写存档。</summary>
	private string _lastSavedCat;
	private int _lastSavedChapter = -1;

	/// <summary>
	/// ★ 本次"进入关卡选择"是否已经恢复过小类。
	///
	/// 旧版用的是"每个大类只恢复一次（整个游戏进程）"——那是**错的**，也正是第一次实机
	/// 测试"退出到主菜单再点墓碑还是原样"的直接原因：第一次进关卡选择时把 `_restored[cat]`
	/// 记上了，之后回主菜单再进来就被自己挡掉了。
	/// ⇒ 改成**每次离开关卡选择场景就清零**，语义变成"每次进来允许恢复一次"，
	///   既能在重复进入时生效，又不会跟主动返回大类选择的玩家抢控制权。
	/// </summary>
	private bool _restoredThisEntry;

	/// <summary>连续多少帧没找到 LevelChoose 节点（用来稳健判定"已离开关卡选择"）。</summary>
	private int _outFrames;

	/// <summary>墓碑按钮扫描探针：记录上次的"找到数量"，只在变化时输出（避免刷屏）。</summary>
	private int _lastFoundCount = -1;

	/// <summary>"已停在大类页面"这条只报一次。</summary>
	private bool _reportedMainCategoryMode;

	/// <summary>"修复 isEditor 泄漏"这条只报一次（v1.4.0）。</summary>
	private bool _editorLeakFixed;

	/// <summary>本次进入章节选择页是否已经"钉住 -1"（阻止 `_Ready()` 自动展开成关卡列表）。</summary>
	private bool _pinnedThisEntry;

	/// <summary>本次进入章节选择页是否已经做过"自动定位到记录章节"。</summary>
	private bool _autoLocatedThisEntry;

	/// <summary>`chapterMenu` 上一帧的子项数（用来判定 `InitChapter()` 是否已收尾）。</summary>
	private int _chapterItemCount;

	/// <summary>自动定位的重试帧数（压住 `SetChildPos` 把新子项摆到最右端的竞争）。</summary>
	private int _autoLocateRetry;

	/// <summary>"找不到按钮"的整树实情：**按场景名**去重（每进一个新场景允许报一次）。</summary>
	private readonly System.Collections.Generic.HashSet<string> _treeDumpReported
		= new System.Collections.Generic.HashSet<string>();

	/// <summary>每进一个新场景打一条心跳（按场景名去重）。</summary>
	private readonly System.Collections.Generic.HashSet<string> _sceneHeartbeat
		= new System.Collections.Generic.HashSet<string>();

	/// <summary>本轮扫描到的"路径 → 实际大类名"，只用于诊断输出。</summary>
	private readonly System.Collections.Generic.List<string> _lastCats
		= new System.Collections.Generic.List<string>();

	private string DumpCats()
	{
		return (_lastCats.Count == 0) ? "<无>" : string.Join(",", _lastCats);
	}

	// ================================================================ 入口三回调

	
	// ============================================================ 配置面板接线
	//
	// mod.json 里声明了 enableLog（bool，默认 false）。
	// 这里在 Initialize 读一次 —— 老宿主没有 context.Settings 时吞异常、按 false 处理。
	// 用 Get<object> 而不是 Get<bool>：宿主把值存成 Dictionary<string, object>，
	// 直接 Get<bool> 可能抛 InvalidCastException。

	/// <summary>
	/// 读「落地层」设置：
	///   chapterList = 停在章节选择页（并把上次章节滚到正中）—— 便于自己挑关卡；
	///   chapter     = 直达章节（自动展开上次的小类，直接看到关卡列表）。
	/// ⚠️ 注意：本 Mod 记忆的是**章节（小类）**，没有记录到具体关卡 ——
	///    用户要的「直达关卡」需另实现（见计划 R62）。
	/// </summary>
	private static void ApplyJumpTargetSetting(XWModRuntimeContext context)
	{
		try
		{
			if (context == null) { return; }
			object v = context.Settings.Get<object>(KJumpTarget);
			string s = null;
			if (v is string str) { s = str; }
			else if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String) { s = je.GetString(); }
			if (string.IsNullOrWhiteSpace(s)) { return; }
			s = s.Trim();
			if (s.Equals("chapter", StringComparison.OrdinalIgnoreCase))
			{
				// ★ v1.4.2：旧值 chapter 与 level 行为重复（都只到关卡列表）⇒ 归并到 level
				JumpToMainCategoryPage = false;
				_wantLevel = true;
			}
			else if (s.Equals("level", StringComparison.OrdinalIgnoreCase))
			{
				JumpToMainCategoryPage = false;   // 直达关卡：先展开章节
				_wantLevel = true;                // 再延后选中具体关卡
			}
			else if (s.Equals("chapterList", StringComparison.OrdinalIgnoreCase))
			{
				JumpToMainCategoryPage = true;    // 停在章节选择页
			}
		}
		catch { }
	}

	private static bool ReadLogSetting(XWModRuntimeContext context)
	{
		try
		{
			if (context == null)
			{
				return false;
			}
			object v = context.Settings.Get<object>("enableLog");
			if (v is bool b)
			{
				return b;
			}
			if (v is string s && bool.TryParse(s, out bool sb))
			{
				return sb;
			}
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

public void Initialize(XWModRuntimeContext context)
	{
			try { SponsorQrRoot = context.PackageRoot; } catch { }
			try
			{
				if (context?.Settings != null && !_settingsSubscribedLqj)
				{
					context.Settings.Changed += OnRuntimeSettingsChanged;
					_settingsSubscribedLqj = true;
				}
			}
			catch { }
			EnableInfoLog = false;   // ★ 2026-10-14 用户要求：默认关闭所有日志（原：EnableInfoLog = ReadLogSetting(context)）
			ApplyJumpTargetSetting(context);   // 落地层：章节页 or 直达章节

		try
		{
			_context = context;
			_faultReported = false;
			string root = (context == null) ? "<null>" : context.PackageRoot;
			Info("初始化完成；PackageRoot=" + root
				+ "。点墓碑将直接进入上次打开的小类关卡。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(LogPrefix + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Warn("拿不到 SceneTree，功能不可用。");
				return;
			}
			_tick = Callable.From(new Action(OnProcessFrame));
			_tree.Connect("process_frame", _tick);
			_graveDown = Callable.From(new Action<string>(OnGraveButtonDown));
			_connected = true;
			Info("已挂载 process_frame 回调。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(LogPrefix + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void Shutdown()
	{
		try
		{
			if (_connected && _tree != null && GodotObject.IsInstanceValid(_tree)
				&& _tree.IsConnected("process_frame", _tick))
			{
				_tree.Disconnect("process_frame", _tick);
			}
			_connected = false;
			Info("已卸载。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(LogPrefix + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	// ================================================================ 每帧

	private void OnProcessFrame()
	{
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "墓碑直达上次关卡", SponsorQrRoot, "Resources/SponsorQR.png"); } catch { }
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree) || _tree.Root == null)
			{
				return;
			}
			if (!EnsureReflection())
			{
				return;
			}

			// ★ 每进一个新场景打一条心跳（按场景名去重）——用来确认"每帧代码到底跑到了哪些场景"。
			//   这是排查"探针为什么沉默"的最直接手段。
			try
			{
				Node sc = GodotObject.IsInstanceValid(_tree) ? _tree.CurrentScene : null;
				string sk = (sc == null) ? "<null>" : sc.Name.ToString();
				if (_sceneHeartbeat.Add(sk))
				{
					Diag("心跳：进入场景 " + sk + "（" + (sc == null ? "?" : sc.GetType().Name) + "）");
				}
			}
			catch { }
			object global = _pGlobal.GetValue(null);
			if (global == null)
			{
				return;
			}

			// ★★★ v1.4.0：修复 `Global.isEditor` 状态泄漏（用户反馈第 3 个问题）。
			//   与本 Mod 的"墓碑直达"无关，但同属**关卡导航状态**，且症状就出在
			//   「进过自制关卡选择页 → 退出 → 任意关卡 → 暂停」这条链路上，
			//   所以放在这里统一修掉（详见方法注释）。
			FixStaleEditorFlag(global);

			// ⓪ 主界面：给墓碑按钮挂 ButtonDown。
			//   主路径仍是下面的"每帧写回"（见那里的时序说明）；这里是**抢同一帧的先后**：
			//   `ButtonDown` 在 `Pressed` 之前发，先一步把 currentChapterId 写回去，
			//   游戏的墓碑回调随后即使再写 -1，也还有下一帧的兜底。
			HookGraveButtons(global);

			// 当前是否在"关卡选择"场景（靠节点类名判断，不依赖 SceneManager 的字段名）
			bool inLevelChoose = CachedLevelChoose() != null;
			if (!inLevelChoose)
			{
				// 离开关卡选择 ⇒ 等连续 N 帧确认真的走了，再清"本次已恢复"标记，
				// 这样回主菜单后再进来还能恢复一次（否则第二次进来会被自己挡掉）。
				// ⚠️ 用计数而不是"一帧没找到就清"：换场景瞬间可能有一帧拿不到节点。
				_outFrames++;
				if (_outFrames >= 3)
				{
					_restoredThisEntry = false;
					_pinnedThisEntry = false;
					_autoLocatedThisEntry = false;
					_chapterItemCount = 0;
					_autoLocateRetry = 0;
					_lastSavedCat = null;
					_lastSavedChapter = -1;
				}
				return;
			}
			_outFrames = 0;

			string cat = Str(_pLevelChoose.GetValue(global));
			if (string.IsNullOrEmpty(cat))
			{
				return;
			}
			int chapterId = Int(_pChapterId.GetValue(global), -1);

			// ★★ 开关打开时（默认）：墓碑点进去停在**大类/章节选择页**，
			//    并且**自动定位（高亮 + 滚动）到记录的那个章节** —— 两件事都要做。
			//
			// ── 需求（2026-10-01 用户明确）──────────────────────────────
			//   ① 停在**章节选择页**（不要直接展开成关卡列表）；
			//   ② 不要让人手动滚动章节 —— **自动定位到记录的那个章节**。
			//
			// ── 怎么同时做到 ──────────────────────────────────────────
			//   · 停在第 1 层：只要**不**让 `LevelChoose._Ready()` 看到
			//     `currentChapterId != -1` 就行（它那条分支会自动展开成关卡列表）：
			//         else if (!IsModBrowser && currentChapterId != -1
			//                  && enterLevelMode == "LevelChoose") Select(currentChapterId);
			//     ⇒ 刚进场景那几帧把它钉在 -1（`_pinnedThisEntry`）。
			//
			//   · 自动定位：等场景**自己初始化完**（`currentMode` 已是 Chapter 且
			//     章节项已生成）之后，主动做两件事：
			//       (a) 反射调 `chapterMenu.SetPos(want)` —— `ChapterMenu` 就挂着
			//           `DragMenu.cs` 脚本，`SetPos` 会把各个章节项平移到该章节居中
			//           （`child.Position = (i - index) * interval`），**这是"不用手动滚动"的关键**；
			//       (b) 调 `LevelChoose.Select(want)` —— 游戏自己会把相机平移到关卡区并
			//           高亮该章节（`Select` 首行有 `tween.IsRunning()` 早退保护）。
			//
			//   ⚠️ 绝不能在 `_Ready()` 之前调 `Select()`：那时 `currentChapterList` 还是
			//     null，`currentChapter = currentChapterList[id]` 会抛。
			//     所以这里用"场景就绪后"的判据兜住（见下 `chaptersReady`）。
			if (JumpToMainCategoryPage)
			{
				// ① 记下玩家选的章节（在关卡列表里选的）
				if (chapterId >= 0)
				{
					if (chapterId != _lastSavedChapter || cat != _lastSavedCat)
					{
						_lastSavedCat = cat;
						_lastSavedChapter = chapterId;
						WriteSaved(cat, chapterId);
						try { WriteSaved(cat, CurrentLevelId(), LevelKeyPrefix); } catch { }   // ★ 并列记关卡
						Diag("记住小类：大类=" + cat + " 章节=" + chapterId
							+ "（回读=" + ReadSaved(cat) + "）");
					}
					_pinnedThisEntry = false;   // 玩家自己操作了 ⇒ 不再钉 -1
					return;
				}

				// ② 刚进场景：先钉住 -1，阻止 `_Ready()` 自动展开成关卡列表
				if (!_pinnedThisEntry)
				{
					_pinnedThisEntry = true;
					Diag("已进入章节选择页（开关=开）：先阻止自动展开，随后自动定位到记录章节");
				}
				if (chapterId != -1)
				{
					SetInt(_pChapterId, global, -1);
				}
				int wantPending = ReadSaved(cat);

				// ③ 章节菜单建好后 → 自动定位到记录的章节
				if (wantPending < 0 || !ChapterMenuReady())
				{
					return;
				}
				int wantLv = wantPending;
				if (wantLv >= ChapterCountSafe())
				{
					_autoLocatedThisEntry = true;
					Diag("无需自动定位：大类=" + cat + " 记忆=" + wantLv
						+ " 章节数=" + ChapterCountSafe());
					return;
				}
				// ★★★ 2026-10-01 第十次实机定案（用户："我现在怎么滑动不了章节了"）：
				//   **一旦定位完成就必须彻底停手**，否则每帧 `SetPos` 会把章节菜单
				//   强行拽回记录的那一章 ⇒ 玩家怎么拖都会被弹回去 = "滑不动"。
				//   （`SetPos` 会重写 `currentIndex/currentPos` 并把所有子项位置钉死，
				//     等于每帧抵消掉 `DragMenu._Input` 里累积的拖动位移。）
				//   ⇒ ① 已完成就直接 return，不再调用；
				//     ② 重试次数封顶，封顶后标记完成；
				//     ③ 未完成期间**一旦检测到玩家正在拖动**（`mousePress`）也立刻停手，
				//        把控制权交还玩家。
				if (_autoLocatedThisEntry)
				{
					return;
				}
				if (ChapterMenuDragging())
				{
					_autoLocatedThisEntry = true;
					Diag("检测到玩家正在拖动章节菜单 ⇒ 停止自动定位，交还控制权");
					return;
				}
				// ★ 连续多帧反复 SetPos：`DragMenu.SetChildPos` 会把**每个新加进来的子项**
				//   摆到最右端，若 `InitChapter()` 比我们晚一帧收尾，单次 SetPos 会被它推回去。
				//   所以重试几帧 —— 但**有上限、且完成即停**（见上）。
				//
				// ★★★ 绝对不要在这里调 `SelectChapter()`！
				//   用户反馈"你这怎么显示的是关卡选择页面？" —— 根因就是那一行。
				//   `Select(id)` 在 Chapter 态下干的是"**进入关卡页**"：
				//       currentChapterIndex = id;
				//       chapterMenu.Set("alive", false);
				//       tween(camera.Y → levelChooseMarker.Y, 0.5);
				//       currentMode = LevelChooseMode.Level;   // ← 切页
				//       InitLevel(id);                         // ← 渲染关卡列表
				//   用户要的是**停在章节页、章节已预选**，所以只调 `SetPos`。
				//   而 `SetPos` 本身就带"预选"的视觉 —— `DragMenu._PhysicsProcess` 里：
				//       scale    = 1.5 - 0.5  * |x| / interval    // 居中的那项放大
				//       modulate = 1.0 - 0.75 * |x| / interval    // 越靠边越淡
				//   ⇒ 被滚到正中的章节自动**放大 + 高亮**，两旁缩小变淡。
				ScrollChapterMenuTo(wantLv);
				_autoLocateRetry++;
				if (_autoLocateRetry >= 12)
				{
					_autoLocatedThisEntry = true;   // ← 关键：置位后上面的守卫会让我们彻底停手
					Diag("已把章节菜单定位到第 " + wantLv + " 章并保持放大高亮"
						+ "（大类=" + cat + " 章节项=" + _chapterItemCount
						+ " 重试=" + _autoLocateRetry + " 帧；未调用 Select，故仍停在章节页）");
				}
				return;
			}

			if (chapterId >= 0)
			{
				// 玩家在关卡选择里挑了一个小类 ⇒ 记住它（只在变化时写盘）
				if (chapterId != _lastSavedChapter || cat != _lastSavedCat)
				{
					_lastSavedCat = cat;
					_lastSavedChapter = chapterId;
					WriteSaved(cat, chapterId);
					// 直出（不受 EnableInfoLog 门控）：这是"记没记住"的唯一证据
					try
					{
						Diag("记住小类：大类=" + cat + " 章节=" + chapterId
							+ "（已写入存档键 " + SaveKeyPrefix + cat + "，回读=" + ReadSaved(cat) + "）");
					}
					catch { }
				}
				return;
			}

			// chapterId == -1：刚从墓碑进来（主菜单把它清掉了）⇒ 恢复上次的小类。
			//
			// ⚠️ 光把 `Global.currentChapterId` 写回去**不够**（第一次实机无效的第二个原因）：
			//    `LevelChoose._Ready()` 读的是它执行那一瞬的值，主菜单已经清成 -1、
			//    `_Ready()` 也就走完了；我们再写回去时它不会重新 `Select()`。
			//    ⇒ 除了写回变量，**还要直接调节点的 `Select(id)`**，完全不依赖时序。
			//
			// ⚠️⚠️ 2026-10-01 第三次实机定案：**不要再拿 `currentMode == Chapter` 当门槛**。
			//    实测日志显示 `记住小类` 有、`准备恢复`／`恢复小类` 一条都没有 ——
			//    就是被 `IsInChapterMode()` 挡掉的：进场景瞬间 `currentMode` 未必是 0
			//    （`_Ready()` 里那条 `Select(AdventureChapterIndex)` 分支可能已经把它推成 Level）。
			//    ⇒ 改成**只靠"本次进入只恢复一次"**（`_restoredThisEntry`）：
			//      玩家按「返回」回到大类选择时，标记还在，不会被打扰；
			//      回主菜单再进来，标记在离开场景时已清零，于是又能恢复。
			if (_restoredThisEntry)
			{
				return;   // 本次进入关卡选择已经恢复过 ⇒ 不再打扰（含玩家主动返回大类选择）
			}
			int want = ReadSaved(cat);
			if (want < 0)
			{
				return;   // 这个大类还没有记忆，保持游戏原样（显示大类选择）
			}
			_restoredThisEntry = true;
			// 直出探针（不受 EnableInfoLog 门控）：走到这里说明条件都满足，
			// 接下来只剩"越界检查"和"Select 调没调成"，打一条便于对账。
			try
			{
				Diag("准备恢复：大类=" + cat + " 记忆章节=" + want
					+ " 当前大类章节数=" + ChapterCountSafe()
					+ " currentMode=" + GetMember(FindNodeByClassName(_tree.Root, "LevelChoose"), "currentMode"));
			}
			catch { }
			if (want >= ChapterCountSafe())
			{
				Info("记忆的章节 " + want + " 超出当前大类的章节数，跳过。");
				return;
			}
			SetInt(_pChapterId, global, want);
			SelectChapter(want);
			if (_wantLevel) { _pendingLevelId = ReadSaved(cat, LevelKeyPrefix); TrySelectLevelDeferred(cat, 0); }
			// 用 GD.Print 直出（不受 EnableInfoLog 门控）——这是核心行为，用户要能自证
			try
			{
				Diag("恢复小类：大类=" + cat + " 章节=" + want
					+ "（主菜单原本清成了 -1，已写回并直接 Select）");
			}
			catch { }
		}
		catch (Exception ex)
		{
			if (!_faultReported)
			{
				_faultReported = true;
				Warn("每帧维护异常（只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// 按**精确路径**找节点，四条通道依次试（都会打诊断，直到命中为止）。
	///
	/// 2026-10-01 第五次实机：`GetNodeOrNull` 两条通道都返回 null（日志恒 `找到=0/7`），
	/// 所以这里补上"枚举实际子树"的兜底与诊断 —— 不再靠猜节点结构。
	/// </summary>
	private Node FindByPathOrTail(Node root, string path)
	{
		if (root == null || string.IsNullOrEmpty(path))
		{
			return null;
		}
		string tail = path;
		int slash = path.LastIndexOf('/');
		if (slash >= 0)
		{
			tail = path.Substring(slash + 1);
		}

		// ① 直接从 root 解析（root 若已是场景根则命中）
		try
		{
			Node direct = root.GetNodeOrNull(path);
			if (direct != null)
			{
				return direct;
			}
		}
		catch { }

		// ② 逐个一级子节点当"场景根"再拼路径（正常场景树：Root → MainMenu → Background/…）
		try
		{
			int n = root.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				Node child = root.GetChild(i);
				if (child == null)
				{
					continue;
				}
				Node hit = child.GetNodeOrNull(path);
				if (hit != null)
				{
					return hit;
				}
			}
		}
		catch { }

		// ③ 兜底：按叶子名**递归枚举**找（比较用 ToString()，不用 StringName == string）
		Node byName = FindNodeByName(root, tail);
		if (byName != null)
		{
			return byName;
		}

		// ④ 全都失败 ⇒ 报整棵树的实情，**按"场景名+目标名"去重**（每场景每目标报一次）。
		//
		// ⚠️⚠️ 踩坑记录（第六/七次实机）：先后用 `_treeDumpReported.Add(按钮名)`、
		//   `.Add(场景名)` 去重，都会让**真正需要的那一次**沉默掉。
		//   ⇒ 现在用「场景名|目标名」组合键：同一场景里 7 个目标各能报一次，够用且不刷屏。
		string sceneKey;
		try
		{
			Node sc = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.CurrentScene : null;
			sceneKey = ((sc == null) ? "<null>" : sc.Name.ToString()) + "|" + tail;
		}
		catch { sceneKey = "<err>|" + tail; }
		if (_treeDumpReported.Add(sceneKey))
		{
			try
			{
				Diag("找按钮失败[" + tail + "]：CurrentScene=" + sceneKey
					+ "｜Root 直属子节点（" + root.GetChildCount() + " 个）：" + DumpChildren(root)
					+ "｜子树里的按钮名：" + DumpButtons(root, 0, 60));
			}
			catch { }
		}
		return null;
	}

	/// <summary>列出某节点的**直属子节点**名字+类型（定位"场景到底挂在 Root 的哪一层"）。</summary>
	private static string DumpChildren(Node node)
	{
		var sb = new System.Text.StringBuilder();
		try
		{
			int n = node.GetChildCount();
			for (int i = 0; i < n && i < 40; i++)
			{
				Node c = node.GetChild(i);
				if (c == null)
				{
					continue;
				}
				sb.Append(c.Name.ToString()).Append(':').Append(c.GetType().Name).Append(' ');
			}
		}
		catch { }
		return sb.Length == 0 ? "<无>" : sb.ToString();
	}

	/// <summary>枚举子树里所有 Button/TextureButton/BaseButton 的名字，用于诊断（最多 max 个）。</summary>
	private static string DumpButtons(Node node, int depth, int max)
	{
		var sb = new System.Text.StringBuilder();
		DumpButtonsInto(node, depth, max, sb);
		return sb.Length == 0 ? "<一个都没找到>" : sb.ToString();
	}

	private static void DumpButtonsInto(Node node, int depth, int max, System.Text.StringBuilder sb)
	{
		if (node == null || depth > 8 || sb.Length > 3000)
		{
			return;
		}
		try
		{
			if (node is BaseButton)
			{
				sb.Append(node.Name.ToString());
				sb.Append(' ');
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				DumpButtonsInto(node.GetChild(i), depth + 1, max, sb);
			}
		}
		catch { }
	}

	/// <summary>
	/// 按**节点名**递归找（兜底通道）。
	/// ⚠️ 名字比较一定用 `node.Name.ToString()` 再比 string ——
	///   `Node.Name` 是 `StringName`，直接与 `string` 比在实机上不可靠。
	/// </summary>
	private static Node FindNodeByName(Node node, string name)
	{
		if (node == null)
		{
			return null;
		}
		try
		{
			if (node.Name.ToString() == name)
			{
				return node;
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				Node r = FindNodeByName(node.GetChild(i), name);
				if (r != null)
				{
					return r;
				}
			}
		}
		catch { }
		return null;
	}

	/// <summary>
	/// 给主界面的墓碑按钮挂一层 `ButtonDown`（按下即触发，早于 `Pressed`）。
	///
	/// 为什么不怕"重复挂"：按实例 id 记账，一个按钮只挂一次；
	/// 主菜单重建（新实例）时 id 会变，自动重新挂。
	/// </summary>
	// ─────────────────────────────────────────────────────────────────────────────
	// ★★ 性能（2026-10-12 真机探针定位）：**这才是那 21ms/帧的真凶**（不是 CachedLevelChoose）。
	//
	// 本方法被 `OnProcessFrame` **每帧**调用，内部对 `GraveButtons` 里的 **7 个**墓碑按钮
	// 各调一次 `FindByPathOrTail(_tree.Root, …)`。而该方法在 ① `GetNodeOrNull(path)`、
	// ② 逐一级子节点再拼路径 都失败后，会落到 ③ **`FindNodeByName(root, tail)` —— 整树递归按名枚举**。
	// 战斗场景里根本没有墓碑按钮 ⇒ **每一帧 7 次整树递归**。
	//
	// 真机实测（诊断构建采样）：本 Mod 回调 均 21.02ms/帧、中位 18.60、p95 48.21、最大 100.21，
	// 而当时 12 个 Mod 回调合计 26.8ms、帧周期 38.6ms ⇒ **它一个占全帧的一半**。
	// （我第一轮误修了同一函数里的 `CachedLevelChoose`，数字没变就是这个原因。）
	//
	// 修法：墓碑按钮**只存在于主菜单** ⇒
	//   ① 7 个全部已挂钩、且节点都还存活时，直接返回（主菜单重建 ⇒ IsInstanceValid 失效 ⇒ 自动恢复扫描）；
	//   ② 否则最多每 15 帧扫一次（首次挂钩延迟 ≤0.25s，体感无差）。
	// ─────────────────────────────────────────────────────────────────────────────
	private readonly System.Collections.Generic.List<Node> _graveBtnNodes =
		new System.Collections.Generic.List<Node>(16);   // 上次扫描命中的按钮节点
	private ulong _graveScanFrame;
	/// <summary>
	/// 扫描间隔（帧）：找到按钮时 15 帧（主菜单新建后快速挂钩）；
	/// **一个都没找到时退避到 60 帧** —— 战斗/关卡选择里本来就没有墓碑按钮，
	/// 每次扫描都是 7 次整树递归白跑。真机实测：15 帧节流时本 Mod 仍占 1.865ms/帧
	/// （中位只有 0.249ms，说明就是这 7 次查找周期性冒出来的尖峰）。
	/// </summary>
	private ulong _graveScanInterval = 15UL;

	/// <summary>是否"7 个墓碑按钮都已挂钩且节点仍存活"（都成立就不必再扫）。</summary>
	private bool GraveAllHookedAndAlive()
	{
		try
		{
			if (_graveBtnNodes.Count < GraveButtons.Count)
			{
				return false;
			}
			for (int i = 0; i < _graveBtnNodes.Count; i++)
			{
				Node n = _graveBtnNodes[i];
				if (n == null || !GodotObject.IsInstanceValid(n))
				{
					return false;
				}
				if (!_hooked.Contains(n.GetInstanceId()))
				{
					return false;
				}
			}
			return true;
		}
		catch { return false; }
	}

	private void HookGraveButtons(object global)
	{
		try
		{
			if (_mGetConnections == null)
			{
				return;
			}
			// ★ 性能闸门（见上面长注释）
			if (GraveAllHookedAndAlive())
			{
				return;
			}
			ulong _gFrame = Engine.GetProcessFrames();
			if (_graveScanFrame != 0UL && _gFrame - _graveScanFrame < _graveScanInterval)
			{
				return;
			}
			_graveScanFrame = _gFrame;

			int found = 0, hooked = 0, missing = 0;
			_lastCats.Clear();
			_graveBtnNodes.Clear();
			foreach (System.Collections.Generic.KeyValuePair<string, string> kv in GraveButtons)
			{
				Node root = _tree.Root;
				Node btn = FindByPathOrTail(root, kv.Key);
				if (btn == null || !GodotObject.IsInstanceValid(btn))
				{
					missing++;
					continue;
				}
				found++;
				_graveBtnNodes.Add(btn);
				ulong id = btn.GetInstanceId();
				if (_hooked.Contains(id))
				{
					continue;
				}
				string cat = ReadCategoryOfButton(btn, kv.Value);
				_lastCats.Add(cat);
				string catLocal = cat;   // 闭包捕获（循环变量不能直接捕）
				// ⚠️ Godot 4 的 `Callable` **没有** `Bind()`（那是 Godot 3 的写法）。
				//   要带参数就用"无参 Action + 闭包"现造一个 Callable。
				Callable cb = Callable.From(new Action(delegate { OnGraveButtonDown(catLocal); }));
				btn.Connect("button_down", cb);
				_hooked.Add(id);
				hooked++;
			}
			// 首次进入主菜单（或按钮集合变化）时直出一次扫描结果。
			//
			// ★★★ 2026-10-01 第四次实机定案：**这里必须"只在变化时"报，不能"只在前几帧报"**。
			//   原来写成 `hooked > 0 || (_hookProbeFrames++ == 0)` —— 结果那条探针
			//   打在第 0 帧（那时还在 Loading 场景，主菜单还没建）⇒ 恒显示 `找到=0/7`，
			//   之后主菜单建好了也不再打 ⇒ **我完全看不到"到底挂上没有"**，
			//   白白多绕一轮。而且 `_hooked` 的 instanceId 会随主菜单重建而变化，
			//   所以每次 `found != _lastFoundCount` 都是一次"新菜单刚建好"，正好该报。
			// ★★★ 2026-10-01 第六次实机定案：**只在"找到数量变化"时报会漏掉关键信息**。
			//
			//   实测日志只在 Loading 场景打了 `找到=0/7` 一条，之后再没有 ——
			//   因为 `found` 从 0 变成 7 时确实会打，但**我看不到**：那条日志被
			//   `_lastFoundCount` 的去重逻辑和"探针只在变化时打"叠在一起，
			//   在"进主菜单 → 挂钩成功"这一最关键的时刻反而沉默。
			//
			//   ⇒ 改成"**每次从 0 变成 >0 就打一条**"（= 每次新进主菜单、成功挂钩都留痕），
			//     这才是真正需要的那条证据。其它变化不刷屏。
			_graveScanInterval = (found > 0) ? 15UL : 60UL;   // ★ 自适应退避

			if (found > 0 && _lastFoundCount <= 0)
			{
				Diag("墓碑按钮挂钩成功：找到=" + found + "/" + GraveButtons.Count
					+ " 新挂=" + hooked + "｜大类名=" + DumpCats());
			}
			else if (found == 0 && _lastFoundCount > 0)
			{
				Diag("已离开主菜单（找得到 0 个墓碑按钮）");
			}
			_lastFoundCount = found;
		}
		catch { }
	}

	/// <summary>
	/// 章节菜单是否**已经真正建好**（`chapterMenu` 下已经有稳定的章节项）。
	///
	/// ⚠️⚠️ 2026-10-01 第八次实机定案：原来用 `currentChapterList` 非空当判据，
	///   但 `_Ready()` 里是**先** `currentChapterList = …`（L131）**再** `InitChapter()`（L133）
	///   才 `AddChild` 章节项（L197）。而 `DragMenu._Ready()` 挂了
	///   `ChildEnteredTree += SetChildPos`，**每个新加进来的子项都会被摆到最右端**
	///   ⇒ 只要我在 `InitChapter()` 跑完之前调 `SetPos`，后面几个 `AddChild`
	///     就又把位置推回去了，视觉上**永远停在第一项**（正是用户看到的现象）。
	///   ⇒ 判据改成"**章节项数量连续两帧不变**"：数量还在涨就说明 `InitChapter` 没跑完。
	/// </summary>
	private bool ChapterMenuReady()
	{
		try
		{
			Node menu = GetChapterMenu();
			if (menu == null)
			{
				return false;
			}
			int n = menu.GetChildCount();
			if (n <= 0)
			{
				_chapterItemCount = 0;
				return false;
			}
			if (n != _chapterItemCount)
			{
				_chapterItemCount = n;
				return false;      // 还在生成章节项
			}
			return true;           // 连续两帧数量不变 ⇒ 建好了
		}
		catch { return false; }
	}

	private Node GetChapterMenu()
	{
		try
		{
			Node lc = FindNodeByClassName(_tree.Root, "LevelChoose");
			if (lc == null || !GodotObject.IsInstanceValid(lc))
			{
				return null;
			}
			return GetMember(lc, "chapterMenu") as Node;
		}
		catch { return null; }
	}

	/// <summary>
	/// 玩家此刻是否**正按着鼠标拖动章节菜单**（`DragMenu.mousePress`）。
	///
	/// 用途：自动定位的重试期间，只要玩家一上手就立刻停手，
	/// 否则我们每帧 `SetPos` 会把他拖出来的位移抹掉 ⇒ "滑不动"。
	/// </summary>
	private bool ChapterMenuDragging()
	{
		try
		{
			Node menu = GetChapterMenu();
			if (menu == null)
			{
				return false;
			}
			object mp = GetMember(menu, "mousePress");
			return (mp is bool b) && b;
		}
		catch { return false; }
	}

	/// <summary>
	/// `LevelChoose` 是否**已经初始化完**（`currentChapterList` 已赋值、`currentMode` 已是 Chapter）。
	///
	/// ⚠️ 这是调用 `Select()` 的**前置条件**：`_Ready()` 之前 `currentChapterList` 还是 null，
	///   `Select()` 里 `currentChapterList[id]` 会抛。
	/// </summary>
	private bool ChapterListReady()
	{
		try
		{
			Node lc = FindNodeByClassName(_tree.Root, "LevelChoose");
			if (lc == null || !GodotObject.IsInstanceValid(lc))
			{
				return false;
			}
			object list = GetMember(lc, "currentChapterList");
			int cnt = 0;
			if (list is Godot.Collections.Array ga)
			{
				cnt = ga.Count;
			}
			else if (list is System.Collections.ICollection col)
			{
				cnt = col.Count;
			}
			if (cnt <= 0)
			{
				return false;
			}
			object mode = GetMember(lc, "currentMode");
			return (mode is int mv) && mv == 0;   // 0 = LevelChooseMode.Chapter
		}
		catch { return false; }
	}

	/// <summary>
	/// 把**章节菜单**滚到指定章节居中 —— 这是"不用手动滚动"的关键一步。
	///
	/// `LevelChoose.chapterMenu` 节点上挂的就是 `Prefab/GUI/DragMenu/DragMenu.cs`，
	/// 它的 `SetPos(int index)` 会把每个子项平移到 `(i - index) * interval`
	/// ⇒ 目标章节正好居中。游戏自己在 `InitLevel()` 里也用同样手法定位关卡菜单
	/// （`levelMenu.CallDeferred("SetPos", num2)`），这里照抄同一套。
	///
	/// ⚠️ 用 `CallDeferred` 而不是直接调：`SetPos` 里会读 `GetChildCount()` /
	///   改子节点 `Position`，避免与"同帧正在生成章节项"打架（游戏自己也 deferred）。
	/// </summary>
	private void ScrollChapterMenuTo(int index)
	{
		try
		{
			Node lc = FindNodeByClassName(_tree.Root, "LevelChoose");
			if (lc == null || !GodotObject.IsInstanceValid(lc))
			{
				return;
			}
			Node menu = GetMember(lc, "chapterMenu") as Node;
			if (menu == null || !GodotObject.IsInstanceValid(menu))
			{
				return;
			}
			MethodInfo m = menu.GetType().GetMethod("SetPos",
				BindingFlags.Public | BindingFlags.Instance);
			if (m != null)
			{
				m.Invoke(menu, new object[] { index });
				return;
			}
			// 兜底：Godot 侧的延迟调用（与游戏 InitLevel 同款写法）
			menu.CallDeferred("SetPos", index);
		}
		catch { }
	}

	/// <summary>
	/// 读按钮代表的大类：从按钮自身的信号连接里取注册名
	/// （游戏 `Connect("pressed", callable)` 会把形如 "Pressed" 的方法名注册进来，
	///   实测注册名就是 "Adventure"/"Challenge"/…，与 `currentLevelChoose` 同源）。
	/// 取不到就回落到 <see cref="GraveButtons"/> 里的静态表。
	/// </summary>
	private string ReadCategoryOfButton(Node btn, string fallback)
	{
		try
		{
			object arr = _mGetConnections.Invoke(btn, new object[] { new StringName("pressed") });
			if (arr is Godot.Collections.Array ga)
			{
				foreach (object item in ga)
				{
					if (!(item is Godot.Collections.Dictionary d))
					{
						continue;
					}
					object callableObj = d.ContainsKey("callable") ? (object)d["callable"] : null;
					if (callableObj == null)
					{
						continue;
					}
					string s = callableObj.ToString();
					int idx = s.LastIndexOf("::");
					string name = (idx >= 0) ? s.Substring(idx + 2) : s;
					name = name.Trim();
					if (name.Length > 0)
					{
						// 去掉可能的参数括号
						int par = name.IndexOf('(');
						if (par >= 0)
						{
							name = name.Substring(0, par);
						}
						// "AdventureButtonPressed" → "Adventure"
						if (name.EndsWith("ButtonPressed"))
						{
							name = name.Substring(0, name.Length - "ButtonPressed".Length);
						}
						// "MiniGameButtonPresed"（游戏里这个拼写就是 Presed）→ "MiniGame"
						else if (name.EndsWith("ButtonPresed"))
						{
							name = name.Substring(0, name.Length - "ButtonPresed".Length);
						}
						if (name == "MiniGame")
						{
							name = "MiniGames";   // 与 currentLevelChoose 的取值对齐
						}
						if (name.Length > 0)
						{
							return name;
						}
					}
				}
			}
		}
		catch { }
		return fallback;
	}

	/// <summary>
	/// `ButtonDown` 回调：把该大类记住的章节写回 `Global.currentChapterId`。
	///
	/// 两种开关下**都要预置**（2026-10-01 修正）：
	///   · 开关【关】⇒ 这就是主路径：`_Ready()` 看到它非 -1 就自动展开成关卡列表；
	///   · 开关【开】⇒ 只是"先手预置"：`_Ready()` 仍会展开，**但**我们随后在每帧里
	///     把 `currentChapterId` 钉回 -1 并回到章节页 ——
	///     借的是游戏那次 `Select()` 带来的**相机定位 + 章节高亮 + 章节菜单居中**，
	///     正是用户要的"停在章节页但已自动定位到记录章节"。
	/// </summary>
	private void OnGraveButtonDown(string cat)
	{
		try
		{
			if (_pGlobal == null || _pChapterId == null || string.IsNullOrEmpty(cat))
			{
				return;
			}
			object global = _pGlobal.GetValue(null);
			if (global == null)
			{
				return;
			}
			int want = ReadSaved(cat);
			if (want < 0)
			{
				return;
			}
			SetInt(_pChapterId, global, want);
			Diag("墓碑按下：" + cat + " → 预置章节 " + want
				+ (JumpToMainCategoryPage
					? "（开关=开：借游戏的定位/高亮，随后钉回章节页）"
					: "（开关=关：直达关卡列表）"));
		}
		catch { }
	}

	/// <summary>
	/// 玩家此刻是否停在「大类选择」态（`LevelChoose.LevelChooseMode.Chapter == 0`）。
	/// 取不到时返回 **false**（宁可不恢复，也不要跟用户抢控制权）。
	/// </summary>
	private bool IsInChapterMode()
	{
		try
		{
			Node lc = FindNodeByClassName(_tree.Root, "LevelChoose");
			if (lc == null)
			{
				return false;
			}
			object mode = GetMember(lc, "currentMode");
			if (mode == null)
			{
				return true;   // 拿不到就按原逻辑恢复（首次进场景时它一定是 Chapter 态）
			}
			return (mode is int mv) && mv == 0;
		}
		catch { return false; }
	}

	// ================================================================ 存档读写
	//
	// ★★★ 为什么**不用** `GetKeyValue` / `SetKeyValue`（2026-10-01 实机定案，第一次测试无效的真因）
	//
	// `GameSaveManager.GetCategoryValue()`（`GameSaveManager.cs:762`）长这样：
	//
	//     Dictionary categoryDictionary = GetCategoryDictionary(category);
	//     if (!categoryDictionary.ContainsKey(key))
	//         categoryDictionary[key] = initData[key];        // ★ initData = KEY_INIT_DICT
	//     return categoryDictionary[key];
	//
	// 对一个**游戏自己不认识**的键（我们的 `ModLastChapter|X`），`initData[key]` 在 Godot 的
	// `Dictionary` 索引器上会**抛 KeyNotFoundException**（不像 C# 字典那样能 TryGetValue）。
	// ⇒ `GetKeyValue("ModLastChapter|Adventure")` **永远抛**；而我们的 catch 把它吞了，
	//   于是 `ReadSaved` 恒返回 -1 ⇒ **一次都没恢复过**。
	//   写侧 `SetCategoryValue` 同样有这行，但因为写之前 `categoryDictionary` 里已经有键，
	//   `ContainsKey(key)` 为真、不会走到那行 ⇒ 写是好的、读是坏的（最难查的一种不对称）。
	//
	// ⇒ 正确做法：**直接拿 `GetKeyDictionary()` 这个原始 Dictionary 自己读写**，
	//   全程 `ContainsKey` 守卫，绝不碰 `initData`。
	//   持久化：游戏在切场景等时机自己会 `Save()`；我们在写完后再显式调一次 `Save()`
	//   （`GameSaveManager.cs:399`，写的是 `Csharp/save.res`），保证退出游戏也不丢。

	/// <summary>
	/// 取 `GameSaveManager.Instance` —— **先按字段、再按属性**。
	/// （游戏里 `Instance` 两种形态混用：`GameSaveManager.Instance` 是字段，
	///   `Global.Instance` / `TowerDefenseManager.Instance` 是属性，所以这里必须都试。）
	/// </summary>
	private object SaveMgr()
	{
		try
		{
			if (_pSaveMgrField != null)
			{
				return _pSaveMgrField.GetValue(null);
			}
			if (_pSaveMgr != null)
			{
				return _pSaveMgr.GetValue(null);
			}
		}
		catch { }
		return null;
	}

	/// <summary>读 Global.Instance.currentLevelId（关卡 id）；读不到返回 -1。</summary>
	private int CurrentLevelId()
	{
		try
		{
			object g = _pGlobal == null ? null : _pGlobal.GetValue(null);
			if (g == null) { return -1; }
			object v = GetMember(g, "currentLevelId");
			return v == null ? -1 : Int(v, -1);
		}
		catch { return -1; }
	}

	/// <summary>
	/// ★ 延后选中"上次的具体关卡"。
	/// 为什么必须延后：`Select()` 在 **Chapter 态**下调会**切页**（文件内长注释的警告），
	/// 必须等章节展开成关卡列表（`currentMode == Level`）之后才安全。
	/// 做法：用 SceneTreeTimer 连续重试（最多 8 次 × 0.15s），
	/// 成功就停；一直不成功 ⇒ **什么都不做**（自然停在章节页，等价于 chapterList 行为），不报错刷屏。
	/// </summary>
	private void TrySelectLevelDeferred(string cat, int attempt)
	{
		try
		{
			if (_pendingLevelId < 0) { return; }
			if (attempt >= 8) { Diag("选关卡超时，停在章节页（id=" + _pendingLevelId + "）"); _pendingLevelId = -1; return; }
			if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return; }

			Node lc = FindNodeByClassName(_tree.Root, "LevelChoose");
			if (lc != null && GodotObject.IsInstanceValid(lc))
			{
				object mode = GetMember(lc, "currentMode");
				int mi = mode == null ? -1 : Int(mode, -1);
				if (mi == 1)   // Level 态 ⇒ 可以安全选关卡
				{
					try
					{
						System.Reflection.MethodInfo m = lc.GetType().GetMethod("Select",
							System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
							null, new Type[] { typeof(int) }, null);
						if (m != null)
						{
							m.Invoke(lc, new object[] { _pendingLevelId });
							Diag("直达关卡：大类=" + cat + " 关卡 id=" + _pendingLevelId);
							_pendingLevelId = -1;
							return;
						}
					}
					catch { }
				}
			}
			// 还没到 Level 态 ⇒ 再等一会
			int next = attempt + 1;
			SceneTreeTimer timer = _tree.CreateTimer(0.15);
			timer.Timeout += () => TrySelectLevelDeferred(cat, next);
		}
		catch { }
	}

	private void WriteSaved(string cat, int chapterId, string prefix = SaveKeyPrefix)
	{
		try
		{
			object mgr = SaveMgr();
			if (mgr == null || _mGetKeyDict == null)
			{
				return;
			}
			object dictObj = _mGetKeyDict.Invoke(mgr, null);
			if (!(dictObj is Godot.Collections.Dictionary keyDict))
			{
				return;
			}
			keyDict[prefix + cat] = chapterId;
			// 立刻落盘（游戏自己也会存，但别指望时机）
			try { _mSave?.Invoke(mgr, null); } catch { }
		}
		catch { }
	}

	private int ReadSaved(string cat, string prefix = SaveKeyPrefix)
	{
		try
		{
			object mgr = SaveMgr();
			if (mgr == null || _mGetKeyDict == null)
			{
				return -1;
			}
			object dictObj = _mGetKeyDict.Invoke(mgr, null);
			if (!(dictObj is Godot.Collections.Dictionary keyDict))
			{
				return -1;
			}
			string k = prefix + cat;
			if (!keyDict.ContainsKey(k))     // ★ 必须守卫：否则索引器可能抛
			{
				return -1;
			}
			return Int(keyDict[k], -1);
		}
		catch { return -1; }
	}

	/// <summary>
	/// 直接让当前 `LevelChoose` 节点展开指定小类。
	///
	/// 为什么光写 `Global.currentChapterId` 不够：`LevelChoose._Ready()` 只在
	/// `currentAwardMode || (currentChapterId != -1 &amp;&amp; enterLevelMode == "LevelChoose")`
	/// 时才 `Select()`；主菜单把 `currentChapterId` 清成 -1 之后，即使我们在**同一帧稍后**
	/// 写回去，`_Ready()` 也已经跑完了（它读的是那一瞬的值）。第一次实机测试就是栽在这里。
	/// ⇒ 直接调节点的 `Select(id)`（`LevelChoose.cs:286`，public async void），**不依赖任何时序**。
	///
	/// ⚠️ 调用前先看 `currentMode`：若已经不是 `Chapter(0)`（说明游戏自己已经切进去了，
	///    或正在切），就**不再调**——`Select()` 首行虽有 `tween.IsRunning()` 早退保护，
	///    但那个保护会让我们的调用在校验过后被静默跳过、可能留下一个"没切过去"的错觉；
	///    干脆不调，最干净。
	/// </summary>
	private void SelectChapter(int id)
	{
		try
		{
			Node lc = FindNodeByClassName(_tree.Root, "LevelChoose");
			if (lc == null || !GodotObject.IsInstanceValid(lc))
			{
				return;
			}
			object mode = GetMember(lc, "currentMode");
			if (mode != null && (mode is int mi ? mi : 0) != 0)
			{
				return;   // 已经不在"大类选择"态 ⇒ 游戏自己处理过了
			}
			MethodInfo m = lc.GetType().GetMethod("Select",
				BindingFlags.Public | BindingFlags.Instance);
			m?.Invoke(lc, new object[] { id });
		}
		catch { }
	}

	/// <summary>当前大类的章节数（越界保护；取不到返回 int.MaxValue = 不拦）。</summary>
	private int ChapterCountSafe()
	{
		try
		{
			Node lc = FindNodeByClassName(_tree.Root, "LevelChoose");
			if (lc == null)
			{
				return int.MaxValue;
			}
			object list = GetMember(lc, "currentChapterList");
			if (list is Godot.Collections.Array ga)
			{
				return ga.Count;
			}
			if (list is System.Collections.ICollection col)
			{
				return col.Count;
			}
		}
		catch { }
		return int.MaxValue;
	}

	// ================================================================ 反射

	// ============================================================ 反射写入的稳健路径
	//
	// ⚠️ 血泪教训（0.30.0 实测）：
	//   游戏导出时裁剪了 BCL，`PropertyInfo.SetValue(object, object)` 这个**两参重载没了**，
	//   直接调用会抛 `MissingMethodException` —— 而且是**每帧抛**（在 OnProcessFrame 里），
	//   日志被刷爆、写入永远失败。
	//   `MethodInfo.Invoke` 是活的（本文件多处正常使用），所以写属性一律走 setter 的 Invoke。
	//
	// 依次尝试：
	//   ① setter 的 MethodInfo.Invoke      —— 最可靠
	//   ② PropertyInfo.SetValue(obj, v, null)（三参重载，与被裁的两参不是同一个方法）
	//   ③ 都失败则返回 false，由调用方 WarnOnce 报一次（不每帧刷屏）

	private static bool TrySetProperty(PropertyInfo p, object target, object value)
	{
		if (p == null)
		{
			return false;
		}
		try
		{
			MethodInfo setter = p.GetSetMethod(true);
			if (setter != null)
			{
				setter.Invoke(target, new object[] { value });
				return true;
			}
		}
		catch { }
		try
		{
			p.SetValue(target, value, null);
			return true;
		}
		catch { }
		return false;
	}

	/// <summary>写一个 int 属性（写不进去只报一次，不抛）。</summary>
	private static void SetInt(PropertyInfo p, object target, int value)
	{
		if (!TrySetProperty(p, target, value))
		{
			WarnOnce("SetInt", "写入失败（属性被裁剪或无 setter）：" + (p == null ? "null" : p.Name));
		}
	}

	/// <summary>同一句提示只报一次 —— 这些代码在每帧路径上，不节制会把日志刷爆。</summary>
	private static readonly System.Collections.Generic.HashSet<string> _warnedOnce = new System.Collections.Generic.HashSet<string>();
	private static void WarnOnce(string tag, string msg)
	{
		if (!EnableInfoLog) { return; }
		// ★ 2026-10-14：该输出原先绕过日志开关，现统一受 enableLog 控制
		if (_warnedOnce.Add(tag))
		{
			try { GD.PrintErr("[LevelQuickJump] " + msg); } catch { }
		}
	}

	private bool EnsureReflection()
	{
		if (_refReady)
		{
			return true;
		}
		if (_refFailed)
		{
			return false;
		}
		try
		{
			// Global：游戏自己的单例（Core/Global/Global.cs）
			Type tGlobal = FindTypeByName("Global");
			Type tSaveMgr = FindTypeByName("GameSaveManager");
			if (tGlobal == null || tSaveMgr == null)
			{
				_refFailed = true;
				Warn("找不到 Global / GameSaveManager 类型，功能不可用。");
				return false;
			}
			_pGlobal = tGlobal.GetProperty("Instance",
				BindingFlags.Public | BindingFlags.Static);
			_pChapterId = tGlobal.GetProperty("currentChapterId",
				BindingFlags.Public | BindingFlags.Instance);
			_pLevelChoose = tGlobal.GetProperty("currentLevelChoose",
				BindingFlags.Public | BindingFlags.Instance);
			_pEnterLevelMode = tGlobal.GetProperty("enterLevelMode",
				BindingFlags.Public | BindingFlags.Instance);

			// ★★★ 2026-10-01 第二次实机定案：`GameSaveManager.Instance` 是**字段**不是属性！
			//
			//   `Core/GameSaveManager/GameSaveManager.cs:102`
			//       public static GameSaveManager Instance;      ← 字段
			//   而 `Global.Instance`（`Global.cs`）与 `TowerDefenseManager.Instance`
			//   （`TowerDefenseManager.cs:211`）都是 `{ get; private set; }` **属性**。
			//   同一套代码里两种形态混用 ⇒ 只用 `GetProperty` 会拿到 null，
			//   然后 `_pSaveMgr == null` 让整个 Mod 每次调用开头就 return（静默失效）。
			//   ⇒ 字段/属性**两种都试**。
			// 逐级遍历基类找静态字段（GetField(NonPublic|Instance) 不查基类，这里用 DeclaredOnly 手动走）
			const BindingFlags FF = BindingFlags.Public | BindingFlags.NonPublic
				| BindingFlags.Static | BindingFlags.DeclaredOnly;
			for (Type cur = tSaveMgr; cur != null && _pSaveMgrField == null; cur = cur.BaseType)
			{
				_pSaveMgrField = cur.GetField("Instance", FF);
			}
			_pSaveMgr = tSaveMgr.GetProperty("Instance",
				BindingFlags.Public | BindingFlags.Static);

			// 读按钮自身的信号连接（用来反推这个按钮代表哪个大类）
			_tNode = typeof(Node);
			_mGetConnections = _tNode.GetMethod("GetSignalConnectionList",
				BindingFlags.Public | BindingFlags.Instance, null,
				new Type[] { typeof(StringName) }, null);
			// ★ 存档通道：**用 GetKeyDictionary()（原始 Dictionary）+ Save()**，
			//   刻意不用 GetKeyValue/SetKeyValue —— 见 WriteSaved 上方的长注释
			//   （GetCategoryValue 对未知键会抛 KeyNotFoundException，那是第一次测试无效的真因）。
			_mGetKeyDict = tSaveMgr.GetMethod("GetKeyDictionary",
				BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
			if (_mGetKeyDict == null)
			{
				foreach (MethodInfo mi in tSaveMgr.GetMethods(BindingFlags.Public | BindingFlags.Instance))
				{
					if (mi.Name == "GetKeyDictionary" && mi.GetParameters().Length == 0)
					{
						_mGetKeyDict = mi;
						break;
					}
				}
			}
			_mSave = tSaveMgr.GetMethod("Save",
				BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

			bool saveMgrOk = (_pSaveMgrField != null) || (_pSaveMgr != null);
			if (_pGlobal == null || _pChapterId == null || _pLevelChoose == null
				|| !saveMgrOk || _mGetKeyDict == null || _mSave == null)
			{
				_refFailed = true;
				Warn("反射成员缺失：Global=" + (_pGlobal != null)
					+ " currentChapterId=" + (_pChapterId != null)
					+ " currentLevelChoose=" + (_pLevelChoose != null)
					+ " SaveMgr(字段)=" + (_pSaveMgrField != null)
					+ " SaveMgr(属性)=" + (_pSaveMgr != null)
					+ " GetKeyDictionary=" + (_mGetKeyDict != null)
					+ " Save=" + (_mSave != null));
				return false;
			}
			_refReady = true;
			if (StartupSelfCheck)
			{
				try
				{
					Diag("自检：Global.Instance=" + (_pGlobal != null)
						+ " currentChapterId=" + (_pChapterId != null)
						+ " currentLevelChoose=" + (_pLevelChoose != null)
						+ " enterLevelMode=" + (_pEnterLevelMode != null)
						+ " GameSaveManager.Instance(字段)=" + (_pSaveMgrField != null)
						+ "GameSaveManager.Instance(属性)=" + (_pSaveMgr != null)
						+ " GetKeyDictionary=" + (_mGetKeyDict != null)
						+ " Save=" + (_mSave != null)
						+ " GetSignalConnectionList=" + (_mGetConnections != null)
						+ " → 全部命中，功能可用");
					Diag("存档键前缀=" + SaveKeyPrefix
						+ "；登记的墓碑按钮 " + GraveButtons.Count + " 个："
						+ string.Join(",", new System.Collections.Generic.List<string>(GraveButtons.Keys)));
					Diag("内部开关[墓碑后停在大类页面]=" + JumpToMainCategoryPage
						+ "　" + (JumpToMainCategoryPage
							? "【开】停在「大类/章节选择」页，不自动进小类"
							: "【关】直达上次打开的那个小类"));
				}
				catch { }
			}
			Info("反射就绪：Global.Instance / currentChapterId / currentLevelChoose / GameSaveManager。");
			return true;
		}
		catch (Exception ex)
		{
			_refFailed = true;
			Warn("反射初始化失败：" + ex.Message);
			return false;
		}
	}

	/// <summary>
	/// 按类名找类型。**用直接类型引用取程序集**（编译期就绑定了 `PlantsVsZombies.dll`），
	/// 不去猜类型在哪个程序集里。
	///
	/// ⚠️ 2026-10-01 实机血泪：原来是 `typeof(TowerDefenseCharacter).Assembly.GetType("GameSaveManager")`
	/// + 遍历 `GetTypes()` 兜底 —— 结果**还是找不到** `GameSaveManager`
	/// （实机日志：`反射成员缺失：… SaveMgr=False …`），整包逻辑静默失效。
	/// ⇒ 既然 csproj 已经 `Reference` 了游戏程序集，就**直接用 `typeof(...)`**，
	///   让编译器去解析，最可靠。
	/// </summary>
	private static Type FindTypeByName(string name)
	{
		switch (name)
		{
			case "Global": return typeof(Global);
			case "GameSaveManager": return typeof(GameSaveManager);
			default: return null;
		}
	}

	/// <summary>
	/// 取游戏单例（`Instance`）。
	/// ⚠️ 游戏源码里 `Instance` **写法不一致**：`TowerDefenseManager.Instance` /
	///   `Global.Instance` / `SceneManager.Instance` 是**属性**，而
	///   `GameSaveManager.Instance` 是**字段** ⇒ 两种都要试。
	/// </summary>
	private static object GetSingleton(string typeName)
	{
		try
		{
			Type t = null;
			foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try { t = asm.GetType(typeName, throwOnError: false); } catch { }
				if (t != null)
				{
					break;
				}
			}
			if (t == null)
			{
				return null;
			}
			PropertyInfo p = t.GetProperty("Instance",
				BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
			if (p != null)
			{
				return p.GetValue(null);
			}
			FieldInfo f = t.GetField("Instance",
				BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
			if (f != null)
			{
				return f.GetValue(null);
			}
		}
		catch { }
		return null;
	}

	private static object GetMember(object target, string name)
	{
		if (target == null)
		{
			return null;
		}
		try
		{
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic
					| BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (f != null)
				{
					return f.GetValue(target);
				}
			}
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic
					| BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (p != null && p.CanRead)
				{
					return p.GetValue(target);
				}
			}
		}
		catch { }
		return null;
	}

	/// <summary>
	/// `GetKeyDictionary()` 返回的 `Godot.Collections.Dictionary` 取值是 `Variant`；
	/// `currentChapterId` 是 `int`。两种都要能吃。
	///
	/// ⚠️ 2026-10-01 实机踩坑：**不要用 `Convert.ToInt32(object)`** ——
	///   游戏那份 .NET 运行时里 `System.Convert.ToString(object)` 直接
	///   `Method not found`（被裁剪），同族的 `Convert.*(object)` 都不可信。
	///   实测报错：`每帧维护异常：Method not found: 'System.String System.Convert.ToString(System.Object)'`。
	///   （`Convert.ToInt32(object)` 在本作里侥幸还在 —— HealthCooldownLine 用了 67 处且工作正常 ——
	///    但既然同族已有缺失，这里统一改成**显式类型分支**，不再依赖 Convert。）
	/// </summary>
	private static int Int(object v, int fallback)
	{
		try
		{
			if (v == null)
			{
				return fallback;
			}
			if (v is int i)
			{
				return i;
			}
			if (v is Variant va)
			{
				try { return va.AsInt32(); } catch { return fallback; }
			}
			if (v is long l)
			{
				return (int)l;
			}
			if (v is double d)
			{
				return (int)d;
			}
			if (v is float f)
			{
				return (int)f;
			}
			if (v is Enum en)
			{
				return (int)(object)en;
			}
			return fallback;
		}
		catch { return fallback; }
	}

	/// <summary>
	/// 取字符串。**刻意不用 `Convert.ToString(object)`** —— 见 <see cref="Int"/> 的说明
	/// （那个重载在本作运行时里 `Method not found`，正是"每帧维护异常"的真因）。
	/// </summary>
	private static string Str(object v)
	{
		try
		{
			if (v == null)
			{
				return null;
			}
			if (v is string s)
			{
				return s;
			}
			if (v is Variant va)
			{
				try { return va.AsString(); } catch { return null; }
			}
			if (v is StringName sn)
			{
				return sn.ToString();
			}
			return null;
		}
		catch { return null; }
	}

	// ─────────────────────────────────────────────────────────────────────────
	// ★★ 性能（2026-10-12 真机探针定位）：**本 Mod 曾经是全套 Mod 里最贵的一个 —— 21.0ms/帧**
	//
	// 元凶就是下面这一句（在 OnProcessFrame 里**每帧**执行）：
	//     bool inLevelChoose = FindNodeByClassName(_tree.Root, "LevelChoose") != null;
	// `FindNodeByClassName` 是**逐节点递归 + 每节点一次反射式类型名比较**
	// （`IsSubclassNamed` 还要沿 BaseType 链逐级比字符串）。菜单里树小还看不出来，
	// **战斗场景树一大人均 20ms 级** —— 真机采样：均 21.03ms、中位 22.87ms，
	// 而当时 12 个 Mod 的回调合计才 32.5ms、帧周期 51.8ms ⇒ 它一个吃掉全帧的 40%。
	//
	// 修法：**按场景缓存**。关卡选择节点属于某个场景，`CurrentScene` 变了才需要重查
	// （`CurrentScene` 只是一次属性读，几乎免费），节点被销毁时再用 `IsInstanceValid` 兜底，
	// 另外每 60 帧强制复查一次作为保险。每帧成本从 21ms 降到 ~0。
	// ─────────────────────────────────────────────────────────────────────────
	private Node _lcCached;
	private ulong _lcSceneId = ulong.MaxValue;
	private ulong _lcFrame;
	private bool _lcHave;      // 「已缓存过结果」——**包含"没找到"**，见下面注释

	/// <summary>缓存版"取关卡选择节点"：按场景变化/节点失效/每 60 帧复查时才真正走树。</summary>
	private Node CachedLevelChoose()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree) || _tree.Root == null)
			{
				return null;
			}
			ulong f = Engine.GetProcessFrames();
			Node sc = _tree.CurrentScene;
			ulong sid = (sc == null || !GodotObject.IsInstanceValid(sc)) ? 0UL : sc.GetInstanceId();
			// 节点被销毁（换场景）⇒ 缓存作废，立刻重查
			if (_lcCached != null && !GodotObject.IsInstanceValid(_lcCached))
			{
				_lcCached = null;
				_lcHave = false;
			}
			// ⚠️⚠️ 关键教训：**"没找到"也必须算作有效缓存**。
			//   第一版写成 `cachedOk = _lcCached != null && IsInstanceValid(_lcCached)`，
			//   于是在**战斗场景**（本来就没有 LevelChoose 节点）里它恒为 false
			//   ⇒ 每一帧照样重走整树。真机实测：修完这版以后该 Mod 仍占 14.17ms/帧
			//   （中位 6.09 = 菜单帧便宜，p95 42.5 = 战斗帧整树），就是这个原因。
			if (!_lcHave || sid != _lcSceneId || f - _lcFrame >= 60UL)
			{
				_lcSceneId = sid;
				_lcFrame = f;
				_lcHave = true;
				// 优先用引擎原生 find_child（搜索整个在 C++ 里做，比 C# 递归整树快一个数量级），
				// 找不到再退回按类名递归（该类节点名不一定与类名相同）。
				_lcCached = _tree.Root.FindChild("LevelChoose", true, false);
				if (_lcCached == null)
				{
					_lcCached = FindNodeByClassName(_tree.Root, "LevelChoose");
				}
			}
			return _lcCached;
		}
		catch { return null; }
	}

	private static Node FindNodeByClassName(Node node, string className)
	{
		if (node == null)
		{
			return null;
		}
		try
		{
			if (IsSubclassNamed(node.GetType(), className))
			{
				return node;
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				Node r = FindNodeByClassName(node.GetChild(i), className);
				if (r != null)
				{
					return r;
				}
			}
		}
		catch { }
		return null;
	}

	private static bool IsSubclassNamed(Type t, string name)
	{
		for (Type cur = t; cur != null; cur = cur.BaseType)
		{
			if (cur.Name == name)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// 修复游戏的 `Global.isEditor` **状态泄漏**（用户 2026-10-01 反馈的第 3 个问题）。
	///
	/// ── 现象 ────────────────────────────────────────────────────────────
	///   进过「自制关卡选择页面」再退出，之后到任意关卡暂停时，
	///   暂停菜单显示的是「**返回编辑器**」，而图鉴 / 主菜单按钮被隐藏。
	///
	/// ── 根因（读源码，非推测）──────────────────────────────────────────
	///   `DialogBattlePause._Ready()`：
	///       if (Global.Instance.isEditor) {
	///           levelEditorButton.Visible = true;     // 「返回编辑器」
	///           handbookButton.Visible   = false;     // 图鉴
	///           mainMenuButton.Visible   = false;     // 主菜单
	///       }
	///   ⇒ 它**只**看 `Global.isEditor`，**不看当前场景**。
	///   而 `LevelEditorStage._Ready()` 会把它设成 `true`（`LevelEditorStage.cs:120`），
	///   只有三条退出路径会清掉：
	///       `LevelEditorBackButtonPressed`(L421)、`ModLevelsButtonPressed`(L219)、
	///       `DialogMainMenuOption`(L101)。
	///   从别的路径离开编辑器（或中途再进过一次编辑器）⇒ 该标志**留在 true**，
	///   之后所有普通战斗的暂停菜单都会误显示「返回编辑器」。
	///
	/// ── 修法（必须保留合法的"编辑器试玩"）─────────────────────────────
	///   在编辑器里按「测试」试玩时（`LevelTestButtonPressed` L356-366）：
	///       `enterLevelMode = "DiyLevel"` + `isEditor = true` → 进 TowerDefense。
	///   **此时暂停菜单应该显示「返回编辑器」**（这是正当功能，绝不能误清）。
	///   ⇒ 判定：只有 **不在编辑器场景** 且 `enterLevelMode` **不属于**
	///      {DiyLevel, LoadLevel, OnlineLevel}（= `HandbookButtonPressed` 里
	///      按「返回编辑器」会回到 `LevelEditorStage` 的那三种）时，才清掉。
	///
	///   安全依据：游戏自己的编辑器判定**几乎全部**是
	///      `Global.isEditor && SceneManager.CurrentScene == "LevelEditorStage"`
	///   双条件（`TowerDefenseManager` L968、`PacketBank` L645、`MapControl` L35…），
	///   所以在非编辑器场景清掉 `isEditor` 不影响任何编辑器逻辑。
	/// </summary>
	private void FixStaleEditorFlag(object global)
	{
		try
		{
			object cur = GetMember(global, "isEditor");
			if (!(cur is bool ed) || !ed)
			{
				return;      // 已是 false ⇒ 无事可做
			}
			// ① 正在编辑器场景里 ⇒ 正常，别动。
			//   ⚠️ 用**两个独立信号**判断，任一说是编辑器就放过（宁可漏修，绝不误清）：
			//     (a) 场景树当前场景的节点名；
			//     (b) 游戏自己的 `SceneManager.Instance.currentScene`
			//         （源码里大量这样用，如 `ShovelManager.cs:285`）。
			string scene = "";
			try
			{
				Node sc = GodotObject.IsInstanceValid(_tree) ? _tree.CurrentScene : null;
				scene = (sc == null) ? "" : sc.Name.ToString();
			}
			catch { }
			string sceneMgr = "";
			try
			{
				sceneMgr = (GetMember(GetSingleton("SceneManager"), "currentScene") as string) ?? "";
			}
			catch { }
			if (scene == "LevelEditorStage" || sceneMgr == "LevelEditorStage")
			{
				return;
			}
			// ② 编辑器发起的战斗（试玩 / 载入 / 联机）⇒ 保留「返回编辑器」
			string mode = "";
			try { mode = (GetMember(global, "enterLevelMode") as string) ?? ""; } catch { }
			if (mode == "DiyLevel" || mode == "LoadLevel" || mode == "OnlineLevel")
			{
				return;
			}
			// ③ 其余 ⇒ 泄漏，清掉
			SetMemberBool(global, "isEditor", false);
			if (!_editorLeakFixed)
			{
				_editorLeakFixed = true;
				Info("已修复 Global.isEditor 状态泄漏（场景=" + scene
					+ " SceneManager=" + sceneMgr
					+ " enterLevelMode=" + mode
					+ "）：原本会让暂停菜单误显示「返回编辑器」。");
			}
		}
		catch { }
	}

	/// <summary>
	/// 写 bool 成员（属性优先、字段兜底）。
	/// `Global.isEditor` 是 `public bool isEditor { get; set; }`（属性）。
	/// </summary>
	private static void SetMemberBool(object target, string name, bool v)
	{
		if (target == null)
		{
			return;
		}
		try
		{
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic
					| BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
				{
					// ★ 不能用 PropertyInfo.SetValue(object, object) —— 该重载被游戏裁剪，0.30.0 上抛 MissingMethodException
		if (!TrySetProperty(p, target, v))
		{
			WarnOnce("SetValue", "属性写入失败（裁剪）：" + name);
		}
					return;
				}
			}
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic
					| BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (f != null && f.FieldType == typeof(bool))
				{
					// FieldInfo.SetValue 也做保护：被裁剪时报告而不是每帧抛
		try
		{
			f.SetValue(target, v);
		}
		catch (Exception ex)
		{
			WarnOnce("SetField", "字段写入失败：" + name + "：" + ex.GetBaseException().Message);
		}
					return;
				}
			}
		}
		catch { }
	}

	private void Info(string msg)
	{
		if (!EnableInfoLog)
		{
			return;
		}
		try { Diag(msg); } catch { }
	}

	private void Warn(string msg)
	{
		if (!EnableInfoLog) { return; }
		// ★ 2026-10-14：该输出原先绕过日志开关，现统一受 enableLog 控制
		try { GD.PrintErr(LogPrefix + msg); } catch { }
	}
	private bool _settingsSubscribedLqj;

	private void OnRuntimeSettingsChanged(System.Collections.Generic.IReadOnlyList<string> keys)
	{
		try
		{
			// ★ 配置保存后重读（各 Mod 的应用逻辑保持既有入口）
			OnRuntimeSettingsChangedImpl(keys);
		}
		catch { }
	}


	private void OnRuntimeSettingsChangedImpl(System.Collections.Generic.IReadOnlyList<string> keys)
	{
		if (_context == null) { return; }
		try { EnableInfoLog = ReadLogSetting(_context); } catch { }
		ApplyJumpTargetSetting(_context);   // ★ jumpTarget 保存后立即重读生效
	}
}
