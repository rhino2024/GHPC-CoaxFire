using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(CoaxFire.CoaxFireMod), "空格直接开副武器机枪", "1.3.1", "rhino2024")]
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
    // 俯角上 ⇒ 一开火就打车边几十米的泥地。
    //
    // 1.2.0 用「每帧 module.SetAimVector(RoofGun, 主炮方向, true)」治这条，结果用户实测出更严重的 BUG：
    // 切到机枪瞄准镜后视角转不动、只能重开任务。IL 定位到真因（fcs.txt IL_00F3 那条支路）：
    //   FireControlSystem::SetAimVector 在按世界点稳定时先把方向换算成目标点
    //     目标点 = ReferenceTransform.position + dir * (CurrentRange / |dir|)
    //   再 FinalSetAimWorldPosition(目标点) → AimPoint=目标点、_aimWorldVector=(目标点-枢轴).normalized。
    // 车顶那套火控从没被玩家测过距，CurrentRange 还是 0 ⇒ 目标点退化回枢轴本身 ⇒ AimPoint 贴在枢轴上、
    // _aimWorldVector 被写成**零向量**。零是吸收态：set_AimWorldVector 只做 value.normalized（零归一还是零），
    // ClampVectorAdjust 的 Vector3.RotateTowards(from=零, ...) 也返回零，连玩家自己用鼠标那条
    // AdjustAimByRatio 的 WorldPoint 支路（fcs.txt IL_01E2）转的都是 AimPoint−枢轴＝零向量 ⇒ 再也回不了非零；
    // 而机枪瞄准镜的机位正是 UsableOptic::LateUpdate 里 LookAt(镜位 + AimWorldVector*1024) 算出来的
    // ⇒ 视角彻底钉死，只有换场景（重建 FCS，Awake 重新播种 ReferenceTransform.forward）才解。
    // 日志里那句 roof gun aim error 0.0 deg 全是 0.0 就是这条的签名：Vector3.Angle(dir, 零) 恒等于 0。
    //
    // 1.3.0 换成 public 的 FireControlSystem::SetAimWorldPosition(点)：它走的就是上面那条末尾，
    // 向量 = (点 − 枢轴).normalized，**完全不乘这台火控自己的 CurrentRange** ⇒ 只要给的点不在枢轴上就不可能
    // 写出零，还顺手把已经归零的镜子救回来。每次写完再回查向量是否塌成零：塌了就按原方向重写一次并拉黑这台火控。
    //
    // 1.3.0 那个点优先取主炮火控的 AimPoint，用户实测出第二个 BUG：切回主炮之后车顶那挺不再跟着玩家瞄。
    // 真因＝AimPoint 只在按世界点稳态（_ActualStabMode==2）里每帧重算；本机 M1A1 的车顶那挺 stab=0，
    // fcs.txt:1534 那段 Update 根本不走，AimPoint 就停在上一发测距留下的陈旧世界点上。车顶枢轴随车转、随车动，
    // 「枢轴→旧点」这条方向越漂越远，喂给 SetAimWorldPosition 的其实是几天前的瞄准方向
    // （CoaxFire.debug.log 实测：aim error 0.2°→153°，pointOffset 从 1201.7 m 收到 675 m，stab=0 range=0.0）。
    // 1.3.1 不再读 AimPoint：一律「车顶枢轴 + 主炮当前瞄准方向 × 主炮当前标尺（拿不到用 400 m）」，
    // 方向每帧现取，车怎么转都跟着；日志同时报「写进去的向量夹角」和「炮口实际指向与视线的夹角」，
    // 前者≈0 只证明命令生效，后者才说明玩家瞄的地方真打得到。

    public sealed class CoaxFireMod : MelonMod
    {
        static MelonPreferences_Category cat;
        static MelonPreferences_Entry<bool> eEnabled, eDebugLog, eCoaxial, eRoofGun, eAimRoof;
        static MelonPreferences_Entry<KeyCode> eToggle, eFireKey;

        static readonly FieldInfo FullControlField =
            HarmonyLib.AccessTools.Field(typeof(GHPC.Player.PlayerInput), "_hasFullVehicleControl");
        static readonly FieldInfo SeatField =
            HarmonyLib.AccessTools.Field(typeof(GHPC.Player.PlayerInput), "_controlledPosition");

        // 这个属性的名字带下划线，程序集里不是 public（本机 csc 实测 CS1061），只能反射；它只用于日志
        static readonly MethodInfo ActualStabModeGetter =
            PropGetter(typeof(GHPC.Weapons.FireControlSystem), "_ActualStabMode");

        static MethodInfo PropGetter(Type t, string name)
        {
            try
            {
                var p = HarmonyLib.AccessTools.Property(t, name);
                return p == null ? null : p.GetGetMethod(true);
            }
            catch { return null; }
        }

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

        // 这一次任务里被证明「写一次就把瞄准向量清成零」的火控实例，换场景才重新试
        static readonly List<int> poisoned = new List<int>();

        // 给车顶火控造目标点用的距离：只要这个点不在它自己的枢轴上，它算出来的瞄准向量就不可能是零
        const float AimDistance = 400f;

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
                "Aim the roof gun's own fire control at whatever the main gun fire control is aiming at, which is what keeps the AA gun "
                + "from ploughing the dirt beside the tank. That fire control is also what drives your machine gun sight, so this is the "
                + "option that used to freeze that sight: the mod now writes it through a world point instead of a vector, which its own "
                + "zero laser range cannot collapse, and after every write it checks whether the aim vector survived - if it did not, the "
                + "vector is put back and that fire control is left alone until the mission restarts. Turn this off if you want the AA gun "
                + "to stay exactly where you last parked it");
            eDebugLog = cat.CreateEntry<bool>("DebugLog", false, "Write the decisions to Bin/Mods/CoaxFire.debug.log; only turn this on when something is wrong");

            if (eDebugLog.Value) OpenLog();
            MelonLogger.Msg("副武器机枪 已加载（按住 " + eFireKey.Value + " 打同轴"
                + (eRoofGun.Value ? "＋车顶" : "") + "机枪，" + eToggle.Value
                + " 开关，" + (eEnabled.Value ? "现在是开的" : "现在是关的") + "）");
            Log("CoaxFire 1.3.1 initialised, enabled=" + eEnabled.Value + " fire key=" + eFireKey.Value
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
            // 归一化向量长度平方约等于 1；太短说明主炮那套自己也坏了，喂过去只会把车顶那套一起拖成零
            if (dir.sqrMagnitude < 0.5f) return;

            // 玩家正从哪套火控驱动的镜子里往外看：那套就是他的视野，别去动
            int lookingId = LookingThroughFcsId();

            // 瞄准点一律沿「主炮那套火控当前的瞄准方向」现算，不再用 aimSource.AimPoint。
            // 1.3.0 拿 AimPoint 是为了让两挺枪收在同一个三维点上，但 AimPoint 只在按世界点稳态
            // （_ActualStabMode==2）里每帧重算；stab=0 的车（M1A1 这类）它就是上一发测距留下的
            // 陈旧世界点。车顶枢轴跟着车转、随车动，「枢轴→旧点」这条方向越漂越远（实测 0.2°→153°、
            // 点距从 1201 m 收到 675 m），切回主炮后车顶那挺还对着老地方打——正是用户报的现象。
            // 距离取主炮当前标尺（测过距就打到那个距离上），拿不到就用 AimDistance。
            float aimDist = AimDistance;
            float mainRange = SafeRange(aimSource);
            if (mainRange >= 50f && mainRange <= 6000f) aimDist = mainRange;

            float worst = -1f;
            string worstName = "-";
            string worstDetail = "-";
            // 真正决定「打不打得到玩家瞄的地方」的是炮口指向，不是我们写进去的向量：
            // 写进去的向量夹角≈0 只证明命令生效，车顶那挺转不转得过去是另一件事
            float worstMuzzle = -1f;
            string worstMuzzleName = "-";
            int written = 0;
            for (int i = 0; i < roofStations.Count; i++)
            {
                var w = roofStations[i];
                if (w == null || w.FCS == null) continue;
                int id = w.FCS.GetInstanceID();
                if (poisoned.Contains(id)) continue;
                if (id == lookingId) continue;

                Vector3 pivot;
                try { pivot = w.FCS.ReferenceTransform.position; }
                catch (Exception e)
                {
                    poisoned.Add(id);
                    Log("no reference pivot on " + w.Name + ": " + e.Message);
                    continue;
                }

                Vector3 pt = pivot + dir * aimDist;
                float pointOffset = aimDist;

                Vector3 before;
                try { before = w.FCS.AimWorldVector; } catch { continue; }
                try
                {
                    w.FCS.SetAimWorldPosition(pt);
                }
                catch (Exception e)
                {
                    Log("aiming " + w.Name + " threw: " + e.Message);
                    continue;
                }
                Vector3 after;
                try { after = w.FCS.AimWorldVector; } catch { continue; }
                written++;

                // 兜底：万一这套火控还是把方向吃成了零，立刻用同一条安全路径按原方向塞回去，
                // 并且本回合再也不碰它——瞄准向量一旦是零就再也转不出来（吸收态）
                if (before.sqrMagnitude > 0.5f && after.sqrMagnitude < 0.5f)
                {
                    bool healed = false;
                    try
                    {
                        w.FCS.SetAimWorldPosition(pivot + before.normalized * AimDistance);
                        healed = w.FCS.AimWorldVector.sqrMagnitude > 0.5f;
                    }
                    catch (Exception e) { Log("healing " + w.Name + " threw: " + e.Message); }
                    poisoned.Add(id);
                    Log("POISON " + w.Name + " fcs=" + SafeFcsName(w.FCS)
                        + " stab=" + StabMode(w.FCS)
                        + " range=" + SafeRange(w.FCS).ToString("0.0")
                        + " |before|2=" + before.sqrMagnitude.ToString("0.000")
                        + " |after|2=" + after.sqrMagnitude.ToString("0.000")
                        + " pointOffset=" + pointOffset.ToString("0.0")
                        + " -> healed=" + healed + ", blacklisted for this mission");
                    ShowNotice("车顶机枪火控写不进（会把机枪瞄准镜钉死），这次任务不再自动瞄它");
                    continue;
                }

                float err = Vector3.Angle(dir, after);
                if (err > worst)
                {
                    worst = err;
                    worstName = w.Name;
                    worstDetail = "stab=" + StabMode(w.FCS)
                        + " range=" + SafeRange(w.FCS).ToString("0.0")
                        + " |aim|2=" + after.sqrMagnitude.ToString("0.000")
                        + " pointOffset=" + pointOffset.ToString("0.0");
                }

                Vector3 muzzle;
                try { muzzle = w.Weapon != null && w.Weapon.MuzzleIdentity != null
                        ? w.Weapon.MuzzleIdentity.transform.forward : Vector3.zero; }
                catch { muzzle = Vector3.zero; }
                if (muzzle.sqrMagnitude > 0.5f)
                {
                    float mErr = Vector3.Angle(dir, muzzle);
                    if (mErr > worstMuzzle)
                    {
                        worstMuzzle = mErr;
                        worstMuzzleName = w.Name;
                    }
                }
            }

            if (worst < 0f) return;
            aimLogTimer -= Time.unscaledDeltaTime;
            if (aimLogTimer <= 0f)
            {
                aimLogTimer = 2f;
                // cmd 收到 0 附近＝命令真的写进去了；muzzle 才是玩家实际看到的差距，
                // 它降不下去＝车顶那挺被俯仰限位或转速拖住，不是我们算错
                Log("roof gun aim error cmd=" + worst.ToString("0.0") + " deg on " + worstName
                    + " muzzle=" + (worstMuzzle < 0f ? "n/a" : worstMuzzle.ToString("0.0") + " on " + worstMuzzleName)
                    + " " + worstDetail + ", from " + aimSourceName + ", aimDist="
                    + aimDist.ToString("0.0") + ", written=" + written
                    + ", poisoned=" + poisoned.Count);
            }
        }

        // 反射取 _ActualStabMode（None=0 Vector=1 WorldPoint=2 Target=3），只为日志；拿不到返回 -1
        static int StabMode(GHPC.Weapons.FireControlSystem fcs)
        {
            if (ActualStabModeGetter == null || fcs == null) return -1;
            try
            {
                object v = ActualStabModeGetter.Invoke(fcs, null);
                return v == null ? -1 : Convert.ToInt32(v);
            }
            catch { return -1; }
        }

        // 玩家当前这个镜头槽背后是哪套火控在驱动视野；认不出来就返回 -1（当没在看）
        static int LookingThroughFcsId()
        {
            try
            {
                var slot = GHPC.Camera.CameraSlot.ActiveInstance;
                if (slot == null) return -1;
                var optic = slot.PairedOptic;
                if (optic == null) return -1;
                var fcs = optic.FCS;
                return fcs == null ? -1 : fcs.GetInstanceID();
            }
            catch { return -1; }
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
            poisoned.Clear();
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
                // 出问题时先看这一行：|aim|2 接近 0 表示这套火控的瞄准向量已经塌了（它的机枪镜正钉死），
                // 我们的写法会把目标点推到枢轴外 400 m，顺带把它救回来
                Log("roof gun " + w.Name + " 自己那套火控 " + SafeFcsName(w.FCS)
                    + "，将跟着 " + aimSourceName + " 的瞄准点转向; stab=" + StabMode(w.FCS)
                    + " CurrentRange=" + SafeRange(w.FCS).ToString("0.0")
                    + " |aim|2=" + SafeAimSq(w.FCS).ToString("0.000")
                    + (SafeAimSq(w.FCS) < 0.5f ? "  <-- 这套的向量现在是零，机枪镜动不了" : ""));
            }
            if (roofStations.Count == 0)
                Log("this vehicle has no roof gun with its own fire control - the aiming pass writes nothing here");
        }

        static float SafeRange(GHPC.Weapons.FireControlSystem fcs)
        {
            try { return fcs == null ? 0f : fcs.CurrentRange; } catch { return 0f; }
        }

        static float SafeAimSq(GHPC.Weapons.FireControlSystem fcs)
        {
            try { return fcs == null ? 0f : fcs.AimWorldVector.sqrMagnitude; } catch { return 0f; }
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
            poisoned.Clear();
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
