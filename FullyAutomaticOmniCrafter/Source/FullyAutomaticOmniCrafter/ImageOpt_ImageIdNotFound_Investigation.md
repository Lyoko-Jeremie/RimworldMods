# Image Opt `Image with id ... not found` 问题调查报告

## 1. 调查对象

目标日志：

```text
[ImageOpt] Image with id -116858 not found
[Prepatcher, 0Harmony.dll] MonoMod.Utils.DynamicMethodDefinition.UnityEngine.StackTraceUtility.ExtractStackTrace_Patch1()
[Prepatcher, 0Harmony.dll] MonoMod.Utils.DynamicMethodDefinition.Verse.Log.Warning_Patch1(System.String)
[Image Opt, ImageOpt.dll] ImageOpt.ImageOpt+<>c.<FlushLogger>b__5_1(System.Byte* msg)
[Image Opt, ImageOpt.dll] ImageOpt.NativeMethods.flush_log(...)
[Image Opt, ImageOpt.dll] ImageOpt.ImageOpt.FlushLogger()
[Image Opt, ImageOpt.dll] ImageOpt.ParallelTextureLoadPatch.PrefixV2(...)
[Image Opt, ImageOpt.dll] ImageOpt.ParallelTextureLoadPatch.Prefix(...)
[Prepatcher, 0Harmony.dll] MonoMod.Utils.DynamicMethodDefinition.Verse.ModContentPack.AnyContentLoaded_Patch1(...)
[Core, Assembly-CSharp.dll] Verse.LoadedModManager+<>c.<LoadModContent>b__13_1()
```

被调查模组：

```text
F:\SteamLibrary\steamapps\workshop\content\294100\3543873568
```

Image Opt 日志显示版本为 `0.1.13`。调查使用了模组附带的 C#、Rust 源码、已发布的 `ImageOpt.dll`、本机 `Player.log`，并通过 `rimsage` 核对了 RimWorld 1.6 原版调用逻辑。

## 2. 结论摘要

该警告由 Image Opt 自身产生，不是 RimWorld 原版错误，也不是 FullyAutomaticOmniCrafter 引发的错误。

根本原因是 Image Opt Windows 原生纹理加载线程池存在生命周期缺陷：

1. 工作线程等待新任务时设置了 120 秒超时。
2. 连续 120 秒没有新纹理任务时，工作线程会全部退出。
3. 线程池保存在 `OnceCell` 中，退出后不会重新创建。
4. 后续纹理仍会被加入队列，但已经没有线程消费。
5. 游戏加载结束后，托管代码按 Unity Instance ID 向原生库索取纹理。
6. 原生结果表中不存在这些未处理任务，因而输出 `Image with id ... not found`。
7. Image Opt 随后使用 RimWorld 原版纹理加载器回退加载。

这不是单张纹理丢失。本次 `Player.log` 中存在数千个连续 ID 取回失败，说明原生消费线程在加载过程中整体失效。

推荐修复原生线程等待逻辑，将带 120 秒超时的 `recv_timeout` 改为持续等待的 `recv`。仅过滤日志只能隐藏表象，无法恢复 Image Opt 的异步纹理优化。

## 3. RimWorld 原版调用时机

通过 `rimsage` 查询，RimWorld 1.6 的 `LoadedModManager.LoadModContent` 先依次调用所有启用模组的 `ReloadContent`，然后注册一个 `ExecuteWhenFinished` 回调。在该回调中，游戏逐一调用每个模组的 `ModContentPack.AnyContentLoaded()`：

```csharp
public static void LoadModContent(bool hotReload = false)
{
    LongEventHandler.ExecuteWhenFinished(() => DeepProfiler.Start(nameof(LoadModContent)));
    for (int index = 0; index < runningMods.Count; ++index)
    {
        ModContentPack runningMod = runningMods[index];
        try
        {
            runningMod.ReloadContent(hotReload);
        }
        // 省略异常处理
    }

    LongEventHandler.ExecuteWhenFinished(() =>
    {
        DeepProfiler.End();
        for (int index = 0; index < runningMods.Count; ++index)
        {
            ModContentPack runningMod = runningMods[index];
            if (!runningMod.AnyContentLoaded())
            {
                Log.Error(...);
            }
        }
    });
}
```

原版 `AnyContentLoaded()` 本身只检查是否加载了非翻译内容或翻译内容：

```csharp
public bool AnyContentLoaded()
{
    return AnyNonTranslationContentLoaded() || AnyTranslationsLoaded();
}
```

Image Opt 给这个检查函数添加了 Harmony Prefix。因此，调用栈中的 `AnyContentLoaded()` 是 Image Opt 等待和收集异步纹理的触发点，不是故障源。

## 4. 完整发生链路

### 4.1 创建占位纹理并提交异步任务

文件：

```text
Source\ImageOpt\TextureLoadPatch.cs
```

方法：

```csharp
private static Texture2D _LoadTextureAsyncV2(VirtualFile file)
```

关键代码位于约第 318—342 行：

```csharp
var d = new Texture2D(2, 2, TextureFormat.Alpha8, false, false);

unsafe
{
    fixed (char* p = file.FullPath)
    {
        NativeMethods.enqueue_image(
            (ushort*)p,
            file.FullPath.Length,
            d.GetInstanceID());
    }
}

d.name = Path.GetFileNameWithoutExtension(file.Name);
ParallelTextureLoadPatch.Tasks.Add(d.GetInstanceID(), file);
return d;
```

Image Opt 此时没有同步读取图片，而是创建 2×2 占位纹理，并将以下数据提交给 Rust 原生库：

- 图片完整路径；
- 占位纹理的 Unity Instance ID。

托管侧同时使用相同 ID 将 `VirtualFile` 保存在 `ParallelTextureLoadPatch.Tasks` 中，供失败时回退加载。

### 4.2 原生任务入队

文件：

```text
Source\ImageOpt\image_lib\src\v2.rs
```

约第 97—103 行：

```rust
#[unsafe(no_mangle)]
pub unsafe extern "C" fn enqueue_image(ptr: *const u16, len: i32, id: i32) {
    THREAD_POOL.get_or_init(create_thread_pool);
    let slice = unsafe { std::slice::from_raw_parts(ptr, len as usize) };
    let str = String::from_utf16(slice).unwrap();
    let path = PathBuf::from(str);
    LOAD_QUEUE.0.read().send((path, id)).ok();
}
```

这里存在两个重要特征：

- `THREAD_POOL` 使用 `OnceCell`，只会初始化一次。
- `send(...).ok()` 会静默丢弃发送错误，无法从日志看出任务是否成功进入有效队列。

### 4.3 原生线程消费队列

同一文件约第 226—246 行：

```rust
fn create_thread_pool() -> Mutex<Vec<JoinHandle<()>>> {
    Mutex::new(
        (0..num_cpus::get())
            .map(|_| {
                std::thread::spawn(|| {
                    while let Ok((path, id)) =
                        LOAD_QUEUE.1.recv_timeout(Duration::from_secs(120))
                    {
                        let result = catch_unwind::<_, anyhow::Result<()>>(|| {
                            let data = create_texture_from_file(&path)?;
                            LOADED.pin().insert(id, data);
                            Ok(())
                        })
                        .map_err(|e| anyhow::anyhow!("{e:?}"));

                        if let Err(e) = result.flatten() {
                            warn!("load image {:?}: {e:?}", path.display());
                        };
                    }
                })
            })
            .collect(),
    )
}
```

加载成功后，结果按照 Instance ID 写入全局 `LOADED` 表。

问题在于：`recv_timeout(Duration::from_secs(120))` 发生超时后返回错误，`while let Ok(...)` 条件立即失败，工作线程永久结束。

### 4.4 线程池无法重建

后续再次调用 `enqueue_image()` 时执行：

```rust
THREAD_POOL.get_or_init(create_thread_pool);
```

但 `THREAD_POOL` 是 `OnceCell`。其中即使保存的所有 `JoinHandle` 对应线程都已经结束，单元仍然处于“已初始化”状态，因此不会再次调用 `create_thread_pool()`。

结果是：

- `LOAD_QUEUE` 继续收到图片任务；
- `LOADED` 不再产生对应结果；
- 任务不会自动恢复。

### 4.5 游戏结束内容加载并等待线程

Image Opt 补丁：

```csharp
[HarmonyPatch(typeof(ModContentPack), "AnyContentLoaded")]
public static class ParallelTextureLoadPatch
```

Windows 下进入 `PrefixV2`。首次执行时：

```csharp
if (!Once)
{
    Once = true;
    NativeMethods.wait_load_images();
}
```

Rust `wait_load_images()` 位于 `v2.rs` 约第 122—129 行：

```rust
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wait_load_images() {
    let (tx, _) = crossbeam::channel::bounded(0);
    drop(std::mem::replace(&mut *LOAD_QUEUE.0.write(), tx));
    let mut handles = THREAD_POOL.get_or_init(create_thread_pool).lock();
    for handle in handles.drain(..) {
        handle.join().unwrap();
    }
}
```

这个函数会主动替换并关闭原发送端，然后等待工作线程退出。也就是说，工作线程本来可以一直使用阻塞式 `recv()`；正常结束机制已经存在，没有必要另外设置 120 秒空闲超时。

### 4.6 按 ID 获取纹理失败

`PrefixV2` 遍历模组的占位纹理并调用：

```csharp
var textureData = NativeMethods.get_texture(id);
```

Rust `get_texture` 位于 `v2.rs` 约第 132—143 行：

```rust
#[unsafe(no_mangle)]
pub unsafe extern "C" fn get_texture(id: i32) -> ImageData {
    if let Some(data) = LOADED.pin().remove(&id) {
        *data
    } else {
        warn!("Image with id {} not found", id);
        ImageData {
            error: 1,
            ptr: std::ptr::null_mut(),
            ..Default::default()
        }
    }
}
```

这就是以下日志字符串的唯一源码发生点：

```text
[ImageOpt] Image with id -116858 not found
```

### 4.7 回退到原版纹理加载

`get_texture` 返回空指针和 `error = 1` 后，C# 代码执行：

```csharp
if ((IntPtr)textureData.ptr == IntPtr.Zero || textureData.error != 0)
{
    texture = TextureLoadPatch.VanillaLoadTexture(Tasks[id]);
    Statistics.fallback += 1;
    Log.Warning($"[ImageOpt] failed to load image {key}. using vanilla texture load");
}
```

因此，同一个失败通常会生成两条不同警告：

```text
[ImageOpt] failed to load image <逻辑纹理名>. using vanilla texture load
[ImageOpt] Image with id <实例ID> not found
```

第一条由 C# 立即输出；第二条先进入 Rust 日志队列，之后才统一刷新，所以两条信息在 `Player.log` 中不一定相邻。

### 4.8 原生日志转发到 Verse

Rust 日志缓冲使用 `SegQueue`：

```rust
pub static LOGGER_BUFFER: SegQueue<(log::Level, String)> = SegQueue::new();
```

`warn!` 宏将警告放入队列。`FlushLogger()` 随后通过回调转发到 RimWorld：

```csharp
NativeMethods.flush_log(
    // 普通日志回调省略
    msg =>
    {
        var str = new string((sbyte*)msg);
        Log.Warning($"[ImageOpt] {str}");
        NativeMethods.free_string(msg);
    },
    // 错误日志回调省略
);
```

文件：

```text
Source\ImageOpt\Main.cs
```

约第 75—95 行。

调用栈中的 `ImageOpt.ImageOpt.FlushLogger()` 是警告进入 Verse 日志系统的位置，而真正创建警告内容的是 Rust `get_texture()`。

## 5. `-116858` 对应纹理

根据 `Player.log` 中同一批回退纹理名和原生 ID 的输出顺序进行配对，ID：

```text
-116858
```

对应的逻辑纹理名为：

```text
Kurin/Apparel/Armor/Tactical_Armor/Texture_Hulk_north
```

这不表示该纹理文件本身损坏。它只是在线程池已经退出后提交或收集的一项任务，因此没有出现在原生 `LOADED` 表中。

## 6. 日志证据

本机日志：

```text
C:\Users\luche\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log
```

观察结果：

- Image Opt 成功加载 Rust 库，版本为 `0.1.13`。
- 日志中存在数千条 `failed to load image ... using vanilla texture load`。
- 日志中存在数千条连续的 `Image with id ... not found`。
- 缺失 ID 大致从 `-107718` 连续延伸至 `-118686`。
- ID 基本按 2 递增，这是 Unity 在持续创建占位 `Texture2D` 时分配实例 ID 的正常表现。
- 大批连续 ID 同时缺失，不符合单张文件损坏，更符合后台消费者停止工作的结果。

日志中另有一项独立的真实图片损坏：

```text
F:\SteamLibrary\steamapps\workshop\content\294100\3736621496\Content\Textures\Things\Building\CustomSign\CustomSignBase.png
```

错误为 PNG `IDAT` 数据块 CRC 校验失败：

```text
Format error decoding Png: CRC error
```

该文件应单独重新下载或修复，但它只能解释自身加载失败，不能解释数千个连续 ID 缺失。

## 7. 已发布 DLL 核对

使用 `ilspycmd 11.0.0.9375` 反编译：

```text
F:\SteamLibrary\steamapps\workshop\content\294100\3543873568\Assemblies\ImageOpt.dll
```

已确认已发布 DLL 中的 `ImageOpt.ParallelTextureLoadPatch.PrefixV2` 与附带源码一致，包括：

- 首次调用 `wait_load_images()`；
- 对每个占位纹理调用 `get_texture(instanceID)`；
- 结果为空时调用 `VanillaLoadTexture`；
- 循环结束后调用 `ImageOpt.FlushLogger()`。

Rust 警告字符串和线程池实现位于附带的原生库源码中。

## 8. 推荐修复方案

### 8.1 首选：移除工作线程空闲超时

修改：

```text
Source\ImageOpt\image_lib\src\v2.rs
```

将：

```rust
while let Ok((path, id)) = LOAD_QUEUE.1.recv_timeout(Duration::from_secs(120)) {
```

改为：

```rust
while let Ok((path, id)) = LOAD_QUEUE.1.recv() {
```

同时删除不再使用的导入：

```diff
- use std::time::{Duration, Instant};
+ use std::time::Instant;
```

修复后的行为：

1. 工作线程在加载期间持续等待任务，不会因为模组加载间隔较长而退出。
2. `wait_load_images()` 替换并丢弃旧发送端。
3. 接收端处理完已排队任务后，`recv()` 因通道断开返回错误。
4. 工作线程正常结束。
5. `wait_load_images()` 的 `join()` 正常完成。

这个方案直接利用了现有的明确关闭流程，是最小且合理的根因修复。

### 8.2 不建议只增加超时时间

例如将 120 秒改为 300 秒或 600 秒只能降低触发概率。大型模组列表、Prepatcher、错误日志、慢速磁盘或其他加载补丁仍可能造成更长空窗期。

只要线程池仍不可重建，任何有限超时都可能再次造成永久失效。

### 8.3 备选：实现可重建线程池

如果作者确实希望工作线程在空闲后自动退出，则需要同时实现完整的线程池状态管理：

- 检查保存的线程是否全部结束；
- 安全回收旧 `JoinHandle`；
- 在新任务到来时重新创建消费者；
- 防止多个提交线程并发重复创建线程池；
- 正确处理队列发送端和接收端生命周期；
- 避免与 `wait_load_images()` 并发重建。

这比直接使用阻塞式 `recv()` 复杂得多，对当前加载阶段没有明显收益。

### 8.4 不要静默忽略入队失败

建议将：

```rust
LOAD_QUEUE.0.read().send((path, id)).ok();
```

改为显式处理错误：

```rust
if let Err(e) = LOAD_QUEUE.0.read().send((path, id)) {
    error!("Failed to enqueue image id {id}: {e}");
}
```

这不是当前线程超时问题的主要修复，但可以避免以后队列关闭或生命周期错误被静默吞掉。

### 8.5 改善任务失败状态

当前工作线程只有成功时才向 `LOADED` 插入结果：

```rust
let data = create_texture_from_file(&path)?;
LOADED.pin().insert(id, data);
```

如果单张图片解码失败，`get_texture(id)` 之后只能看到“ID 不存在”，无法区分：

- 任务从未处理；
- 解码失败；
- GPU 纹理创建失败；
- 结果被提前取走。

可以考虑让 `LOADED` 保存成功或失败状态，并携带原始路径及错误文本。这样托管层能够报告真正原因，而不是二次输出含义模糊的 `id not found`。

## 9. 仅阻止日志出现的方法

以下办法可以阻止目标日志，但不会修复异步加载失败。

### 9.1 在 `FlushLogger()` 中过滤

可以在 warning 回调中加入精确判断：

```csharp
var str = new string((sbyte*)msg);
if (!str.StartsWith("Image with id ", StringComparison.Ordinal) ||
    !str.EndsWith(" not found", StringComparison.Ordinal))
{
    Log.Warning($"[ImageOpt] {str}");
}
NativeMethods.free_string(msg);
```

优点：只屏蔽目标格式。

缺点：纹理仍会回退加载；大量失败依然存在。

### 9.2 修改 Rust 警告级别或删除警告

将：

```rust
warn!("Image with id {} not found", id);
```

删除，或改为不输出。

缺点同样是隐藏症状，且会降低真实故障的可诊断性。

### 9.3 Harmony 过滤 `Verse.Log.Warning`

其他模组可以给 `Verse.Log.Warning(string)` 添加 Prefix，只对以下精确格式返回 `false`：

```text
[ImageOpt] Image with id <整数> not found
```

必须采用严格的前后缀或格式判断，不能屏蔽所有 `[ImageOpt]` 警告，否则真实 PNG、DDS 或 GPU 创建错误也会被隐藏。

### 9.4 禁用 Image Opt

禁用 Image Opt 会彻底避免这些日志，并恢复 RimWorld 原版纹理加载流程，但会失去 Image Opt 提供的 DDS、GPU 纹理创建和相关性能优化。

### 9.5 设置项无法解决

Image Opt 当前没有日志开关。以下设置不能修复线程池生命周期问题：

- `enable_dds`；
- `auto_compress`；
- `image_limit`；
- mipmap bias；
- anisotropic filtering level。

其中 `image_limit` 对应的信号量逻辑在当前源码中已标注为废弃。

## 10. 风险与影响

发生该问题后，Image Opt 通常仍能依靠 `VanillaLoadTexture` 使游戏继续启动，因此它更像性能和日志污染问题，而不一定立即导致游戏崩溃。

主要影响包括：

- 数千条警告及堆栈显著膨胀 `Player.log`；
- Prepatcher/Harmony 堆栈提取进一步增加加载开销；
- Image Opt 后半程纹理异步优化失效；
- 大量纹理回退到同步原版加载；
- 加载时间和内存峰值可能增加；
- 真正的损坏纹理警告被大量重复日志淹没。

## 11. 最终建议

建议按以下顺序处理：

1. 修改 Rust 工作线程，将 `recv_timeout(120s)` 改为 `recv()`。
2. 重新编译 Windows 原生库并替换 `image_lib.dll`。
3. 保留 `wait_load_images()` 的发送端关闭及线程 `join()` 流程。
4. 为 `send()` 增加显式错误日志。
5. 重新启动游戏，确认不再出现连续 `Image with id ... not found`。
6. 单独重新下载或修复 Workshop `3736621496` 中 CRC 损坏的 `CustomSignBase.png`。
7. 如果暂时无法重新编译原生库，再考虑精确过滤目标警告；不要将过滤日志作为最终修复。

最终结论：目标日志的根因是 Image Opt 原生加载线程在 120 秒空闲后永久退出，而非 `-116858` 对应纹理本身缺失。修复线程池生命周期后，既能阻止大批日志出现，也能恢复 Image Opt 预期的异步纹理加载功能。
