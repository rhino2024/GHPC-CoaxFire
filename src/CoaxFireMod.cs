using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(CoaxFire.CoaxFireMod), "空格直接开副武器机枪", "1.2.0", "rhino2024")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace CoaxFire
{
    // 按住空格就直接用同轴机枪／车顶高射机枪开火，不用先把武器切过去，主炮该往哪指还往哪指。
    //
    // 为什么必须做 mod：游戏整个程序集里只有一个开火动作 "Fire"，它永远打在
    // CrewBrainWeaponsModule.ActiveWeapon 上，没有任何"副武器开火"的绑定可以选。
    // 但底层 API 是按角色寻址的：CrewBrainWeaponsModule::Fire(WeaponSystemRole, IUnit) 内部
    // GetWeaponSystemByRole(role).Weapon.Fire(target) 直接取那挺武器，完全不读 _activeWeapon，也不走
    // set_ActiveWeapon 那条链（制导重启、StopFiring(旧炮)、FCS.SetWeaponSystem、HUD 武器名、
    // 自动弹药选择全都不会被触发）。所以"按角色开另一挺炮"是安全的。
    //
    // 为什么车顶机枪也能一起打：CrewBrainWeaponsModule.Weapons 只在
    // CrewManager::OnNewCremMember 里被赋值一次，而且是整辆车的 WeaponsManager.Weapons 数组
    // 原样拷进来（IL_0222/0227：new List(数组)，没有任何按车长／装填手的过滤），
    // 同时只有 Gunner（CrewPosition=2）那个 brain 会拿到这张表 ⇒ 炮手位就看得见车顶机枪那条
    // Role=RoofGun 的记录。反过来车长／装填手的 brain 那张表是 null，所以别去碰别人的 brain。
    //
    // 松开按键必须自己收尾：循环枪声由 WeaponSystem::Fire 内部 SoundController.StartLoop() 开启，
    // 而**只有** WeaponSystem::StopFiring() 会 StopLoop()。扳机自己松开（handleTriggerReleased）
    // 只把 TriggerIsHeld 置假、不再出弹，声音留着不放 ⇒ 每挺我们开过的枪都要自己关一次。
    // module.StopFiring(role) 不能用：它第一行 if(!IsFiring) return，而 IsFiring 是整个 module
    // 共享的一个标志，PlayerInput 自己的收尾会把主炮那次开火的 IsFiring 先清掉。
    //
    // 车顶机枪为什么老打脚下的地板：本机实测武器表是 MainGun=120mm gun M256（火控 GPS）、
    // Coaxial=7.62mm M240（和主炮同一套 GPS）、RoofGun=.50 caliber M48（**另一套 CWS fire
    // control system**）。子弹是从武器自己的炮口出去的（velocity = MuzzleVelocity *
    // MuzzleIdentity.forward），主炮准星管不着车顶那挺；玩家从没操过它的那套火控，它就停在默认
    // 俯角上 ⇒ 一开火就打车边几十米的泥地。修法是把主炮火控的指向每帧喂给车顶火控，走游戏自己的
    // CrewBrainWeaponsModule::SetAimVector（内含 MovementLockedOut 门与 null 保护），它内部
    // FireControlSystem::SetAimVector 再按自己的转限速度 Vector3.RotateTowards 慢慢追过去——
    // 所以这是个渐进过程，必须提前瞄（不只在按开火键那几帧），日志会打当前夹角误差。
    public sealed class CoaxFireMod : MelonMod
    {
        static MelonPreferences_Category cat;
        static MelonPreferences_Entry<bool> eEnabled, eDebugLog, eCoaxial, eRoofGun, eAimRoof;
        static MelonPreferences_Entry<KeyCode> eToggle, eFireKey;

        static readonly FieldInfo FullControlField =
            HarmonyLib.AccessTools.Field(typeof(GHPC.Player.PlayerInput), "_hasFullVehicleControl");
        static readonly FieldInfo SeatField =
            HarmonyLib.AccessTools.Field(typeof(GHPC.Player.PlayerInput), "_controlledPosition");

        static StreamWriter log;
        static string noticeText = "";
        static float noticeTimer;
        static GUIStyle noticeStyle;
        static string warnedAbout = "";
        static int shotsFired;

        // 这一回合我们真的开过火的武器，松手时要逐挺收尾
        static readonly List<GHPC.Weapons.WeaponSystemInfo> guns =
            new List<GHPC.Weapons.WeaponSystemInfo>();
        static readonly List<GHPC.Weapons.WeaponSystemInfo> fired =
            new List<GHPC.Weapons.WeaponSystemInfo>();

        // 需要跟着主炮瞄的车顶枪，和提供指向的那套火控（主炮的）
        static readonly List<GHPC.Weapons.WeaponSystemInfo> roofStations =
            new List<GHPC.Weapons.WeaponSystemInfo>();
        static GHPC.Weapons.FireControlSystem aimSource;
        static string aimSourceName = "-";
        float aimLogTimer;

        string lastVerdict = "-";
        float verdictTimer;
        bool wasFiring;
        static GHPC.Crew.CrewBrainWeaponsModule lastModule;

        public override void OnInitializeMelon()
        {
            cat = MelonPreferences.CreateCategory("CoaxFire", "空格直接开副武器机枪");
            eEnabled = cat.CreateEntry<bool>("Enabled", true, "Master switch. With it off these guns can only be fired the old way, by switching to them first");
            eToggle = cat.CreateEntry<KeyCode>("ToggleKey", KeyCode.F6, "Key that turns the feature on or off during a mission; the current state is printed on screen");
            eFireKey = cat.CreateEntry<KeyCode>("FireKey", KeyCode.Space,
                "Hold this key to fire the secondary machine guns. Note: Space is the game's own Let Commander Aim by default, "
                + "so both actions overlap and the commander override clears the main gun's lead. Rebinding that game action is cleanest, "
                + "or set this fire key to a free one such as LeftAlt or V");
            eCoaxial = cat.CreateEntry<bool>("FireCoaxial", true, "Fire the coaxial machine gun while the fire key is held (Role=Coaxial)");
            eRoofGun = cat.CreateEntry<bool>("FireRoofGun", true,
                "Also fire the roof or loader AA machine gun while the fire key is held (Role=RoofGun). "
                + "Its rounds leave that gun's own muzzle, so where they land depends on the next option aiming its fire control");
            eAimRoof = cat.CreateEntry<bool>("AimRoofGunAlongMainGun", true,
                "Aim the roof gun's fire control where the main gun points, which fixes the AA gun firing into the ground. "
                + "It has its own CWS fire control that the player never touches, so by default it sits at depression and ploughs the dirt; "
                + "with this on, the main gun's direction is fed to it every frame and it follows at its own traverse rate, so a fast swing will lag. "
                + "Switching to the roof gun yourself hands it back to the game");
            eDebugLog = cat.CreateEntry<bool>("DebugLog", false, "Write the decisions to Bin/Mods/CoaxFire.debug.log; only turn this on when something is wrong");

            if (eDebugLog.Value) OpenLog();
            MelonLogger.Msg("副武器机枪 已加载（按住 " + eFireKey.Value + " 打同轴"
                + (eRoofGun.Value ? "＋车顶" : "") + "机枪，" + eToggle.Value
                + " 开关，" + (eEnabled.Value ? "现在是开的" : "现在是关的") + "）");
            Log("CoaxFire 1.2.0 initialised, enabled=" + eEnabled.Value + " fire key=" + eFireKey.Value
                + " coaxial=" + eCoaxial.Value + " roof=" + eRoofGun.Value
                + " aim roof along main gun=" + eAimRoof.Value
                + " toggle=" + eToggle.Value);
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(eToggle.Value))
            {
                eEnabled.Value = !eEnabled.Value;
                try { cat.SaveToFile(false); } catch (Exception e) { Log("saving threw: " + e.Message); }
                ShowNotice(eEnabled.Value
                    ? "副武器机枪 已开启 - 按住 " + eFireKey.Value + " 直接打机枪"
                    : "副武器机枪 已关闭 - 按 " + eToggle.Value + " 恢复");
                Log("feature " + (eEnabled.Value ? "on" : "off"));
            }

            if (noticeTimer > 0f) noticeTimer -= Time.unscaledDeltaTime;

            bool firing = false;
            if (eEnabled.Value && Input.GetKey(eFireKey.Value))
            {
                firing = TryFire();
                if (eDebugLog.Value)
                {
                    verdictTimer -= Time.unscaledDeltaTime;
                    if (verdictTimer <= 0f)
                    {
                        verdictTimer = 1f;
                        Log("key held -> " + lastVerdict);
                    }
                }
            }

            // 只要这一帧没能继续开火（松手、关掉、离开炮手位、进 AAR 都算），就把枪声收掉
            if (!firing && wasFiring) ReleaseGuns();
            wasFiring = firing;

            // 车顶那挺的转速有限，只在按开火键的那几帧才瞄就永远追不上，所以一直瞄着
            if (eEnabled.Value) AimRoofGuns();
        }

        void AimRoofGuns()
        {
            if (!eAimRoof.Value) return;

            var pi = GHPC.Player.PlayerInput.Instance;
            if (pi == null || GHPC.AarController.InAar) return;
            if (!pi.PlayerCanControlGunnerNow || pi.UsingBinoculars) return;

            var brain = pi.CurrentPlayerCrewBrain;
            var module = brain != null ? brain.WeaponsModule : null;
            if (module == null) return;

            // 武器表是按 module 实例缓存的，没按开火键时也要先把车顶那挺找出来
            FindGuns(module, pi);
            if (roofStations.Count == 0 || aimSource == null) return;

            // 玩家自己把武器切到车顶那挺，瞄准就还给游戏，别和它的准星抢
            GHPC.Weapons.WeaponSystemInfo active = null;
            try { active = module.ActiveWeapon; } catch { active = null; }
            if (active != null && active.Role == GHPC.Weapons.WeaponSystemRole.RoofGun) return;

            Vector3 dir;
            try { dir = aimSource.AimWorldVector; } catch { return; }
            if (dir.sqrMagnitude < 0.0001f) return;

            float worst = -1f;
            string worstName = "-";
            for (int i = 0; i < roofStations.Count; i++)
            {
                var w = roofStations[i];
                if (w == null || w.FCS == null) continue;
                try
                {
                    module.SetAimVector(w.Role, dir, true);
                    float err = Vector3.Angle(dir, w.FCS.AimWorldVector);
                    if (err > worst) { worst = err; worstName = w.Name; }
                }
                catch (Exception e)
                {
                    Log("aiming " + w.Name + " threw: " + e.Message);
                }
            }

            if (worst < 0f) return;
            aimLogTimer -= Time.unscaledDeltaTime;
            if (aimLogTimer <= 0f)
            {
                aimLogTimer = 2f;
                // 夹角一路收到 0 附近＝车顶那挺真的在跟着转；一直很大＝它的火控被门挡住了
                // （MovementLockedOut）或者转向上限太慢，那时候要看的是这一行而不是猜
                Log("roof gun aim error " + worst.ToString("0.0") + " deg on " + worstName
                    + ", from " + aimSourceName);
            }
        }

        void ReleaseGuns()
        {
            for (int i = 0; i < fired.Count; i++)
            {
                var w = fired[i];
                try
                {
                    if (w != null && w.Weapon != null)
                    {
                        w.Weapon.StopFiring();
                        Log("trigger released, fire loop stopped on " + w.Name + " (" + w.Role + ")");
                    }
                }
                catch (Exception e)
                {
                    Log("StopFiring threw on " + (w != null ? w.Name : "?") + ": " + e.Message);
                }
            }
            fired.Clear();
        }

        bool TryFire()
        {
            try
            {
                var pi = GHPC.Player.PlayerInput.Instance;
                if (pi == null) { lastVerdict = "no PlayerInput"; return false; }
                if (GHPC.AarController.InAar) { lastVerdict = "in AAR"; return false; }
                if (!pi.PlayerCanControlGunnerNow) { lastVerdict = "gunner control is off"; return false; }
                if (pi.UsingBinoculars) { lastVerdict = "binoculars"; return false; }
                if (FullControlField != null && SeatField != null)
                {
                    bool full = (bool)FullControlField.GetValue(pi);
                    int seat = (int)SeatField.GetValue(pi);
                    if (!full && seat != 2) { lastVerdict = "no vehicle control, seat " + seat; return false; }
                }

                var brain = pi.CurrentPlayerCrewBrain;
                if (brain == null) { lastVerdict = "no crew brain"; return false; }
                var module = brain.WeaponsModule;
                if (module == null) { lastVerdict = "no weapons module"; return false; }

                FindGuns(module, pi);
                if (guns.Count == 0)
                {
                    lastVerdict = "this vehicle has no coaxial or roof machine gun";
                    if (warnedAbout != WeaponListKey(module))
                    {
                        warnedAbout = WeaponListKey(module);
                        Log("no machine gun to drive, weapon table = " + WeaponListKey(module));
                    }
                    return false;
                }

                var active = module.ActiveWeapon;
                int now = 0;
                bool yielded = false;
                for (int i = 0; i < guns.Count; i++)
                {
                    var w = guns[i];
                    if (w == null || w.Weapon == null) continue;

                    // 已经选中这挺枪而且左键也按着：游戏自己那一段正在开它，再叫一次就是重复，
                    // 而且这里也不能去收尾，否则会把玩家正常射击的枪声掐掉
                    if (active != null && active.Role == w.Role && Input.GetMouseButton(0))
                    {
                        yielded = true;
                        continue;
                    }

                    module.Fire(w.Role, null);
                    if (!fired.Contains(w)) fired.Add(w);
                    now++;
                }

                if (now == 0)
                {
                    lastVerdict = yielded ? "the game is already firing them" : "no gun fired";
                    return yielded;
                }

                lastVerdict = "fired " + now + " gun(s)";
                shotsFired += now;
                if (eDebugLog.Value && shotsFired - now < 20)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < fired.Count; i++)
                    {
                        if (i > 0) sb.Append(" + ");
                        sb.Append(fired[i].Name + "(" + fired[i].Role + ")");
                    }
                    Log("firing " + sb + " while active = "
                        + (active != null ? active.Role.ToString() : "-"));
                }
                return true;
            }
            catch (Exception e)
            {
                Log("TryFire threw: " + e.Message);
                return false;
            }
        }

        // 换车／换车组时武器表会变，所以缓存只认同一个 module 实例
        static void FindGuns(GHPC.Crew.CrewBrainWeaponsModule module, GHPC.Player.PlayerInput pi)
        {
            if (module == lastModule) return;
            lastModule = module;
            guns.Clear();
            fired.Clear();
            warnedAbout = "";
            List<GHPC.Weapons.WeaponSystemInfo> list = null;
            try { list = module.Weapons; } catch { list = null; }
            if (list == null) return;

            int mainId = -1;
            try
            {
                var a = module.ActiveWeapon;
                if (a != null && a.FCS != null) mainId = a.FCS.GetInstanceID();
            }
            catch { }

            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (w == null || w.Weapon == null) continue;
                bool coaxial = w.Role == GHPC.Weapons.WeaponSystemRole.Coaxial && eCoaxial.Value;
                bool roof = w.Role == GHPC.Weapons.WeaponSystemRole.RoofGun && eRoofGun.Value;
                if (!coaxial && !roof) continue;
                guns.Add(w);
                // 车顶机枪可能和主炮共用一套火控系统（那样它的指向就被主炮拽着），这里一次看清楚
                string fcsName = "null";
                int fcsId = -1;
                try
                {
                    if (w.FCS != null) { fcsName = w.FCS.name; fcsId = w.FCS.GetInstanceID(); }
                }
                catch { }
                Log("gun ready: " + w.Name + " role=" + w.Role
                    + " muzzle offset=" + w.MuzzleAngleOffset
                    + " fcs=" + fcsName + "#" + fcsId
                    + (fcsId >= 0 && fcsId == mainId ? "  <-- 和主炮是同一套 FCS" : ""));
            }
            Log("weapon table: " + WeaponListKey(module));
            BuildAimPlan(list);
        }

        // 指向从主炮那套火控取（取不到就退回当前选中武器），要瞄的是那几挺自己的火控和它
        // 不是同一个对象的 RoofGun —— 共用一套火控的话根本不用瞄，它本来就跟着主炮转
        static void BuildAimPlan(List<GHPC.Weapons.WeaponSystemInfo> list)
        {
            aimSource = null;
            aimSourceName = "-";
            roofStations.Clear();

            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (w == null || w.FCS == null) continue;
                if (w.Role != GHPC.Weapons.WeaponSystemRole.MainGun) continue;
                aimSource = w.FCS;
                aimSourceName = SafeFcsName(w.FCS);
                break;
            }
            if (aimSource == null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var w = list[i];
                    if (w == null || w.FCS == null) continue;
                    if (w.Role == GHPC.Weapons.WeaponSystemRole.Coaxial)
                    {
                        aimSource = w.FCS;
                        aimSourceName = SafeFcsName(w.FCS);
                        break;
                    }
                }
            }
            if (aimSource == null) { Log("no aim source: no main gun or coaxial FCS in this vehicle"); return; }

            int sourceId = aimSource.GetInstanceID();
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (w == null || w.Weapon == null || w.FCS == null) continue;
                if (w.Role != GHPC.Weapons.WeaponSystemRole.RoofGun) continue;
                if (w.FCS.GetInstanceID() == sourceId)
                {
                    Log("roof gun " + w.Name + " 和主炮共用 " + aimSourceName + "，不用另瞄");
                    continue;
                }
                roofStations.Add(w);
                Log("roof gun " + w.Name + " 自己那套火控 " + SafeFcsName(w.FCS)
                    + "，将跟着 " + aimSourceName + " 转向");
            }
        }

        static string SafeFcsName(GHPC.Weapons.FireControlSystem fcs)
        {
            try { return fcs == null ? "null" : fcs.name + "#" + fcs.GetInstanceID(); }
            catch { return "?"; }
        }

        static string WeaponListKey(GHPC.Crew.CrewBrainWeaponsModule module)
        {
            try
            {
                var list = module.Weapons;
                if (list == null) return "no list";
                var sb = new StringBuilder();
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == null) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(list[i].Role + "=" + list[i].Name);
                }
                return sb.ToString();
            }
            catch (Exception e) { return "list threw: " + e.Message; }
        }

        // ---------------------------------------------------------------- notice

        static void ShowNotice(string text)
        {
            noticeText = text;
            noticeTimer = 2.5f;
        }

        public override void OnGUI()
        {
            if (noticeTimer <= 0f || string.IsNullOrEmpty(noticeText)) return;
            if (noticeStyle == null)
            {
                noticeStyle = new GUIStyle(GUI.skin.label);
                noticeStyle.fontSize = 16;
                noticeStyle.alignment = TextAnchor.MiddleCenter;
            }

            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(noticeTimer * 1.5f));
            GUI.Label(new Rect(0f, Screen.height * 0.07f, Screen.width, 28f), noticeText, noticeStyle);
            GUI.color = old;
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            lastModule = null;
            guns.Clear();
            fired.Clear();
            roofStations.Clear();
            aimSource = null;
            aimSourceName = "-";
            wasFiring = false;
            warnedAbout = "";
            Log("scene loaded: " + sceneName + " - weapon cache dropped");
        }

        // ---------------------------------------------------------------- logging

        static void OpenLog()
        {
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Mods");
                Directory.CreateDirectory(dir);
                log = new StreamWriter(Path.Combine(dir, "CoaxFire.debug.log"), true, new UTF8Encoding(false))
                { AutoFlush = true };
            }
            catch { log = null; }
        }

        internal static void Log(object message)
        {
            if (log == null) return;
            try { log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + message); }
            catch { }
        }

        public override void OnApplicationQuit()
        {
            try { if (log != null) log.Close(); } catch { }
            log = null;
        }
    }
}
