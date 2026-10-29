# 空格直接开副武器机枪

Gunner, HEAT, PC! 的 MelonLoader 模组。按住空格直接打同轴机枪和车顶高射机枪，武器不用切过去，主炮该指哪还指哪。

## 你会看到什么

原版只有一个开火键，而且它永远打在当前选中的那件武器上。想打同轴或车顶机枪，得先把武器切过去，主炮的准星也跟着变了。这个模组在你按住开火键（默认空格）时直接让同轴机枪开火，车顶或装填手的高射机枪一起打；松手就停。当前武器不变，主炮瞄哪儿还是哪儿，HUD 上的武器名也不跳。

车顶那挺的另一个毛病也一起处理了：它有自己的火控，玩家从来操作不到，默认停在俯角上，一开火就往车边几十米的泥地里打。模组每帧把主炮的指向喂给它的火控，它按自己的转速慢慢追过去——所以是逐渐跟上，猛打方向时会慢半拍。

喂过去的是「主炮这一刻指着哪」这条实时方向，不是主炮火控里存着的一个目标点：后者在没开世界点稳定的车（M1A1 这一类）上是上一发测距留下的旧坐标，车顶枢轴跟着车体转，拿它当方向会越打越偏。写法上也不能直接把方向向量塞给那套火控——它会再乘一遍自己的当前距离，而车顶那挺从没测过距，那个距离是 0，方向会被写成零向量，而那台瞄准镜的机位正是沿这条向量摆的，镜头就此钉死，动不了也回不来。现在改成给一个世界坐标点，方向由游戏自己算；你正从车顶那套火控驱动的镜子里往外看时，模组让路，不去动你正在用的视野。

`F6` 随时开关。

## 装

1. 先彻底退出游戏。游戏开着的时候 dll 是锁住的，覆盖不了。
2. 把 zip 解压到游戏根目录，就是放着 `GHPC.exe` 的那一层。解压出来应该看到 `Bin\Mods\CoaxFire.dll`。
3. 开一次游戏。MelonLoader 会在 `Bin\UserData\MelonPreferences.cfg` 里写出 `[CoaxFire]` 这一段。

需要 MelonLoader v0.7.2（net35 运行时），下载包里没有它。它不改游戏任何文件，删掉那个 dll 就算卸载。

## 能改的东西

| 名字 | 默认 | 作用 |
| --- | --- | --- |
| Enabled | true | 总开关，关掉之后机枪只能按原版先切换再打 |
| ToggleKey | F6 | 任务中开关用的键 |
| FireKey | Space | 按住就开机枪的键 |
| FireCoaxial | true | 按住时打同轴机枪 |
| FireRoofGun | true | 按住时连车顶／装填手的高射机枪一起打 |
| AimRoofGunAlongMainGun | true | 把车顶机枪的火控瞄到主炮指的方向 |
| DebugLog | false | 出问题再开，会写 `Bin/Mods/CoaxFire.debug.log` |

## 先说清楚

空格默认也是游戏自己的「车长超控瞄准（Let Commander Aim）」，两个功能会叠在一起，超控会清掉主炮提前量。最干净的办法是在游戏按键设置里把那条改掉，或者把这里的开火键改成 `LeftAlt`、`V` 之类空着的键。

它只开这两挺机枪，主炮还是用原来的开火键。车顶机枪的转速有上限，急着转过去它会追不上，所以要提前瞄。你自己把武器切到车顶那挺的时候，模组让路，由游戏自己瞄。只有主炮、没有机枪的车，按住键什么也不会发生。

## 想自己编译

不用 .NET SDK，也不用 Unity 编辑器，一个 dll 而已，用任何 Roslyn `csc` 对着游戏自带的托管程序集编就行：

```bash
GHPC_DIR="/path/to/Gunner HEAT PC/Bin" bash src/build.sh deploy
```

`deploy` 会把编出来的 dll 覆盖到 `<游戏目录>\Bin\Mods\`，所以先关游戏。不加 `deploy` 就只编到 `src/bin/`。

## In English

Hold Space to fire the coaxial and roof machine guns without switching weapons, so the main gun stays aimed where it was. The roof gun has its own fire control that the player never touches and that sits at depression by default, which is why it used to plough the dirt; this mod feeds the main gun's direction to it every frame and lets it traverse at its own rate. The direction comes from where the main gun is looking right now, not from a target point stored in its fire control, which goes stale on vehicles without world-point stabilisation. Version 1.3.0 also stops the roof gun's own optic from freezing: writing a raw direction vector makes that fire control multiply it by its own range, and since the player never ranges with the roof gun that range is zero, which used to leave the sight pointing at nothing and unable to move. 1.3.1 keeps the roof gun on your reticle after you switch back to the main gun. `F6` toggles it, and the fire key is rebindable. Also note that Space is the game's own Let Commander Aim by default, so rebind one of the two.

MIT，与 Radian Simulations 无关。在当前 Steam 版上实测过，游戏更新有可能让它失效。
