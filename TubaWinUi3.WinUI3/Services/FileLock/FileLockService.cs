using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TubaWinUi3.Services.FileLock;

/// <summary>
/// 一个占用目标路径的进程，以及它持有的相关路径（扫描目录时是具体子文件）。
/// </summary>
public sealed class FileLockEntry
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string ProcessPath { get; init; } = "";

    /// <summary>
    /// 扫描时该进程的启动时间（UTC）。结束进程前用它核对 PID 是否已被复用；读不到时为 null。
    /// </summary>
    public DateTime? StartTimeUtc { get; init; }

    /// <summary>该进程持有的、与目标匹配的路径（已去重，保持发现顺序）。</summary>
    public IReadOnlyList<string> LockedPaths { get; init; } = Array.Empty<string>();
}

/// <summary>扫描失败原因。页面据此给出可本地化的提示。</summary>
public enum FileLockScanError
{
    None,
    EmptyPath,
    NotFound,
    ResolveFailed,
    Failed
}

/// <summary>结束进程前的身份核对结果（页面据此决定是否放行 Kill）。</summary>
internal enum ProcessIdentityCheck
{
    /// <summary>仍是扫描时的那一个进程，可以结束。</summary>
    Match,

    /// <summary>进程已退出（或已打不开）。</summary>
    Exited,

    /// <summary>进程还在但身份对不上：PID 已被复用；或信息不足，无法确认。</summary>
    Mismatch
}

/// <summary>
/// 扫描失败的具体原因（显示层据此翻文案；数值参数见 <see cref="FileLockScanResult.FailureCode"/>）。
/// 服务层不再产用户可见的散文——那会把中英混在一句话里。
/// </summary>
public enum FileLockScanFailure
{
    /// <summary>没有失败。</summary>
    None,

    /// <summary>CreateFileW 打不开目标；FailureCode = Win32 错误码。</summary>
    CreateFileFailed,

    /// <summary>目标不是磁盘上的文件或目录。</summary>
    NotDiskFile,

    /// <summary>拿不到目标的内核设备名。</summary>
    DeviceNameUnavailable,

    /// <summary>定位不到 File 对象类型索引。</summary>
    ObjectTypeIndexUnavailable,

    /// <summary>系统句柄表超出上限，放弃扫描；FailureCode = 需要的缓冲字节数。</summary>
    HandleTableTooLarge,

    /// <summary>NtQuerySystemInformation 失败；FailureCode = NTSTATUS。</summary>
    NtQuerySystemInformationFailed,

    /// <summary>未预期的异常。</summary>
    Unexpected
}

/// <summary>
/// 扫描结果。<see cref="SkippedProcesses"/> / <see cref="Truncated"/> 必须如实呈现给用户，
/// 不能静默吞掉——否则「没扫到」会被误解成「没人占用」。
/// </summary>
public sealed class FileLockScanResult
{
    public IReadOnlyList<FileLockEntry> Entries { get; init; } = Array.Empty<FileLockEntry>();

    /// <summary>实际检查过的句柄数（已按 ObjectTypeIndex 过滤后的外层计数）。</summary>
    public int ScannedHandles { get; init; }

    /// <summary>因权限等原因没能查询的进程数（去重后的 PID 个数）。</summary>
    public int SkippedProcesses { get; init; }

    /// <summary>枚举中途卡住、被看门狗放弃续跑的那几个句柄数（结果可能漏了它们）。</summary>
    public int GuardedHandles { get; init; }

    /// <summary>枚举中途卡住/超时被截断，结果可能不完整。</summary>
    public bool Truncated { get; init; }

    public FileLockScanError Error { get; init; }

    /// <summary>失败的具体原因（Error 为 ResolveFailed / Failed 时有效）。文案由显示层翻译。</summary>
    public FileLockScanFailure Failure { get; init; }

    /// <summary>Failure 的数值参数（Win32 错误码 / NTSTATUS / 需要的缓冲字节数）；无参数为 0。</summary>
    public int FailureCode { get; init; }

    /// <summary>诊断用详情（中文散文，仅供日志与测试）；界面一律走 FileLockErrorText.Describe。</summary>
    public string? ErrorDetail { get; init; }

    public bool Succeeded => Error == FileLockScanError.None;

    public int TotalLockedPaths => Entries.Sum(e => e.LockedPaths.Count);
}

/// <summary>
/// 「文件占用查看」（对标 PowerToys File Locksmith）。
/// 实现要点与取舍见各方法注释；总体策略是**保守**：
/// 宁可少报（GuardedHandles / Truncated 如实计数）也不调用有阻塞风险的查询。
/// 注意「保守」的边界：只对**确实有阻塞风险**的对象退让（管道/设备交给 GetFileType 过滤），
/// 不按 GrantedAccess 整类跳过——那样会把以读写方式打开的文件也一起漏掉。
/// </summary>
public static class FileLockService
{
    // ---------- 卡死防护参数（保守版：不调 TerminateThread） ----------
    private const int WatchdogTickMs = 200;

    /// <summary>连续这么久没有推进就认为卡住，放弃等待并快照已完成结果。</summary>
    private const int StallTimeoutMs = 8000;

    /// <summary>其他分片都已收尾时，给最后一片的宽限期（超过即判定卡住）。</summary>
    private const int IdleAbandonMs = 2000;

    /// <summary>整轮扫描最多容忍几个卡死的句柄——卡住的会被跳过并从下一个位置续跑。</summary>
    private const int MaxStallRecoveries = 4;

    /// <summary>整体硬超时。</summary>
    private const int HardTimeoutMs = 60_000;

    /// <summary>
    /// 进程名读不到时的兜底显示（"PID:1234"）。结束前的身份核对会**跳过**带这个前缀的名字——
    /// 否则拿兜底字符串去和真实进程名比较，会把合法目标误判成「PID 已被复用」。
    /// </summary>
    internal const string ProcessNameFallbackPrefix = "PID:";

    private static int _liveSnapshotBuffers;

    /// <summary>仍占着未释放的句柄表快照数。测试用：扫描（含取消）结束后不应增长。</summary>
    internal static int LiveSnapshotBuffers => Volatile.Read(ref _liveSnapshotBuffers);

    private static int _activeScanRunners;

    /// <summary>仍在跑的分片线程数。测试用：等它归零后再断言快照已释放。</summary>
    internal static int ActiveScanRunners => Volatile.Read(ref _activeScanRunners);

    /// <summary>测试注入点：每处理一个句柄回调一次（生产路径为 null，只多一次判空）。</summary>
    internal static Action<int>? TestPerHandleHook;

    /// <summary>
    /// 句柄表快照的缓冲上限（默认 64MB，约 160 万句柄）。可写是给测试用：
    /// 调小它就能确定性地覆盖「句柄表过大」这条失败路径。
    /// </summary>
    internal static int MaxSnapshotBufferBytes { get; set; } = FileLockNative.MaxHandleBufferSize;

    /// <summary>带原因码的扫描失败：界面把 Failure/FailureCode 翻成文案，不解析异常消息。</summary>
    private sealed class ScanFailureException(FileLockScanFailure failure, int code, string message) : Exception(message)
    {
        public FileLockScanFailure Failure { get; } = failure;
        public int Code { get; } = code;
    }

    public static Task<FileLockScanResult> ScanAsync(string path, CancellationToken cancellationToken, IProgress<string>? progress = null)
        => Task.Run(() => Scan(path, cancellationToken, progress), cancellationToken);

    /// <summary>
    /// 扫描目标文件/目录的占用者。
    ///
    /// 过滤层次（越靠前越便宜，目的是把有阻塞风险的 NtQueryObject 留到最后）：
    /// 1) ObjectTypeIndex == File；2) DuplicateHandle + GetFileType == DISK（这一层把管道、
    /// 设备、事件这类会在 NtQueryObject 上永久阻塞的对象全部挡掉）；3) 与目标同卷
    /// （取不到卷序列号的一律跳过）；4) NtQueryObject 取名字再比路径。
    /// 第 3 层是 v1 的取舍：它挡掉了几乎所有会卡死的设备/远端句柄，代价是
    /// 「拿不到卷序列号但确实占了目标文件」的句柄会被漏报（因此异常时宁可退化为不做该层筛）。
    ///
    /// 刻意**不**按 GrantedAccess 跳过：0x0012019F 不是管道专属值，它就是
    /// FILE_GENERIC_READ|FILE_GENERIC_WRITE（读写打开），按它跳过会把 Excel / Word /
    /// FileStream(ReadWrite) 这类最常见的占用静默漏报——防卡死由第 2 层负责，与访问权限无关。
    ///
    /// 已知限制（v1 刻意不做）：
    /// - 不检测内存映射镜像（Section 对象）：运行中的 exe / 已加载的 DLL 占不出；
    /// - 不开启 SeDebugPrivilege：打不开的进程跳过并计入 SkippedProcesses；
    /// - subst / 符号链接 / 挂载卷等路径别名对不上（按内核设备名精确比较）；
    /// - 极少数句柄仍可能让 NtQueryObject 阻塞：句柄表按片并行枚举，卡住的那一个句柄会被跳过
    ///   并从其后续跑（计入 GuardedHandles）；连片尾都无法续跑时才 Truncated=true。
    ///   不调 TerminateThread，代价是可能泄漏一个后台线程。
    /// </summary>
    public static FileLockScanResult Scan(string path, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new FileLockScanResult { Error = FileLockScanError.EmptyPath };

        string raw = path.Trim();
        bool isDirectory = Directory.Exists(raw);
        if (!isDirectory && !File.Exists(raw))
            return new FileLockScanResult { Error = FileLockScanError.NotFound };

        // 1. 用目标路径打开一个自己的句柄：既拿目标的内核设备名，也用来定位
        //    本启动周期的 File 对象类型索引（ObjectTypeIndex 每次开机稳定）。
        IntPtr ownHandle = FileLockNative.CreateFileW(
            raw, 0, FileLockNative.ShareAll, IntPtr.Zero,
            FileLockNative.OpenExisting, FileLockNative.FileFlagBackupSemantics, IntPtr.Zero);

        if (ownHandle == FileLockNative.InvalidHandleValue)
        {
            int lastError = Marshal.GetLastWin32Error();
            return new FileLockScanResult
            {
                Error = FileLockScanError.ResolveFailed,
                Failure = FileLockScanFailure.CreateFileFailed,
                FailureCode = lastError,
                ErrorDetail = $"CreateFileW 失败（错误码 {lastError}）"
            };
        }

        string targetKernelName;
        ushort fileTypeIndex;
        ulong targetVolumeSerial;
        try
        {
            if (FileLockNative.GetFileType(ownHandle) != FileLockNative.FileTypeDisk)
            {
                return new FileLockScanResult
                {
                    Error = FileLockScanError.ResolveFailed,
                    Failure = FileLockScanFailure.NotDiskFile,
                    ErrorDetail = "目标不是磁盘上的文件或目录"
                };
            }

            targetKernelName = QueryObjectName(ownHandle) ?? "";
            if (targetKernelName.Length == 0)
            {
                return new FileLockScanResult
                {
                    Error = FileLockScanError.ResolveFailed,
                    Failure = FileLockScanFailure.DeviceNameUnavailable,
                    ErrorDetail = "无法解析目标的内核设备名"
                };
            }

            // 句柄表查询在这里（解析阶段）就可能失败（上限 / NtQuerySystemInformation）：
            // 必须折成带原因码的结果，否则异常会逃出 Scan，界面只能贴异常消息原文
            // ——英文界面里就会出现中文散文（review 发现 ④ 的主要失败路径）。
            try
            {
                fileTypeIndex = ResolveFileTypeIndex(ownHandle);
            }
            catch (ScanFailureException ex)
            {
                Debug.WriteLine($"[FileLock] 解析 File 类型索引失败: {ex.Message}");
                return new FileLockScanResult
                {
                    Error = FileLockScanError.Failed,
                    Failure = ex.Failure,
                    FailureCode = ex.Code,
                    ErrorDetail = ex.Message
                };
            }

            if (fileTypeIndex == 0)
            {
                return new FileLockScanResult
                {
                    Error = FileLockScanError.ResolveFailed,
                    Failure = FileLockScanFailure.ObjectTypeIndexUnavailable,
                    ErrorDetail = "无法确定 File 对象类型索引"
                };
            }

            // 同卷预筛用的卷序列号；取不到就退化为「不做这一层筛」（不能退化成「全跳过」）。
            targetVolumeSerial = TryGetVolumeSerial(ownHandle, out ulong serial) ? serial : 0;
        }
        finally
        {
            // 探测用的句柄必须关掉，否则本进程会被算成占用者（自伤式误报）。
            FileLockNative.CloseHandle(ownHandle);
        }

        var deviceMap = BuildDeviceMap();
        var state = new ScanState(targetKernelName.TrimEnd('\\'), isDirectory, fileTypeIndex, targetVolumeSerial, deviceMap);

        try
        {
            RunWithWatchdog(state, cancellationToken, progress);
        }
        catch (OperationCanceledException)
        {
            // 目前 RunWithWatchdog 自己不会抛 OCE（取消只在分片线程里检查并抛出），
            // 但这条分支也要把主线程的名额还回去：将来若在循环里直接抛，才不会永久漏掉快照。
            state.ReleaseSnapshotOwner();
            throw;
        }
        catch (Exception ex)
        {
            // 主线程上的失败（句柄表查询 / 缓冲上限）：折成带原因码的结果，
            // 让界面走本地化文案，而不是把异常消息原样贴出来。
            Debug.WriteLine($"[FileLock] 扫描前置阶段失败: {ex}");
            state.ReleaseSnapshotOwner();
            return new FileLockScanResult
            {
                Error = FileLockScanError.Failed,
                Failure = (ex as ScanFailureException)?.Failure ?? FileLockScanFailure.Unexpected,
                FailureCode = (ex as ScanFailureException)?.Code ?? 0,
                ErrorDetail = ex.Message
            };
        }

        // 取消与看门狗放弃都可能发生在工作线程还在跑的时候，这里统一按「已取消」上抛。
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        var error = FileLockScanError.None;
        var failure = FileLockScanFailure.None;
        int failureCode = 0;
        string? detail = null;
        if (state.Error is OperationCanceledException canceled)
            throw canceled;
        if (state.Error is not null)
        {
            error = FileLockScanError.Failed;
            detail = state.Error.Message;
            failure = (state.Error as ScanFailureException)?.Failure ?? FileLockScanFailure.Unexpected;
            failureCode = (state.Error as ScanFailureException)?.Code ?? 0;

            // 意外失败的详情不再上屏（界面走本地化文案），至少留一条日志便于排查
            Debug.WriteLine($"[FileLock] 扫描失败({failure}): {state.Error}");
        }

        return new FileLockScanResult
        {
            Entries = state.BuildEntries(),
            ScannedHandles = state.ScannedHandles,
            SkippedProcesses = state.SkippedProcesses,
            GuardedHandles = state.GuardedHandles,
            Truncated = state.Truncated,
            Error = error,
            Failure = failure,
            FailureCode = failureCode,
            ErrorDetail = detail
        };
    }

    /// <summary>
    /// 取一份句柄表快照，切成若干片交给多个工作线程并行枚举，主线程做看门狗。
    ///
    /// 为什么要分片 + 续跑：NtQueryObject 在极少数句柄上会长时间阻塞（实测某显卡驱动持有的
    /// 一个读句柄能卡住 10 秒以上）。只有一个工作线程时，一个卡住的句柄会让**整轮扫描**
    /// 拿不到任何结果；分片后只损失它所在的那一片。更进一步：卡住时能读到该片**卡在哪一个
    /// 句柄**，于是把那个句柄记进 GuardedHandles 并从它之后续跑，最终只损失这一个句柄。
    ///
    /// 不调 TerminateThread（会破坏 CLR 运行时），卡住的线程被直接放弃——代价是可能
    /// 泄漏一个后台线程与它持有的少量句柄，换来的是进程绝不假死。
    /// </summary>
    private static void RunWithWatchdog(ScanState state, CancellationToken cancellationToken, IProgress<string>? progress)
    {
        IntPtr buffer = QueryHandleTable(out int count);
        state.AttachSnapshotBuffer(buffer);

        int workerCount = Math.Clamp(Environment.ProcessorCount, 2, 4);
        state.StartInitialSlices(buffer, count, workerCount, cancellationToken, progress);

        var stopwatch = Stopwatch.StartNew();
        int lastProcessed = -1;
        long lastChangeMs = 0;
        int recoveries = 0;

        while (state.PendingRunners > 0 && stopwatch.ElapsedMilliseconds < HardTimeoutMs)
        {
            Thread.Sleep(WatchdogTickMs);

            // 取消优先：工作线程可能正卡在 NtQueryObject 里，看门狗负责让调用方及时返回。
            if (cancellationToken.IsCancellationRequested) break;

            int processed = state.Processed;
            if (processed != lastProcessed)
            {
                lastProcessed = processed;
                lastChangeMs = stopwatch.ElapsedMilliseconds;
                continue;
            }

            long stalledMs = stopwatch.ElapsedMilliseconds - lastChangeMs;

            // 只剩一个分片没动静时基本可以断定它卡住了，不必等满 StallTimeout。
            long limit = state.PendingRunners <= 1 ? IdleAbandonMs : StallTimeoutMs;
            if (stalledMs < limit) continue;

            if (recoveries < MaxStallRecoveries)
            {
                int resumed = state.RecoverStalled(buffer, count, cancellationToken, progress);
                if (resumed > 0)
                {
                    recoveries += resumed;
                    lastChangeMs = stopwatch.ElapsedMilliseconds;
                    continue;
                }
            }

            // 到这儿说明卡住的句柄已经在片尾且无法续跑，只能放弃这一片。
            state.Truncated = true;
            break;
        }

        if (state.PendingRunners > 0)
            state.Truncated = true;

        // 主线程自己的名额到期：它之后不会再拉起任何分片（看门狗循环已结束）。
        // 分片仍在跑（取消 / 看门狗放弃）时快照由最后一个退出的分片释放；
        // 都结束了则在这里当场释放——取消路径的泄漏就是这么收回的。
        state.ReleaseSnapshotOwner();
    }

    /// <summary>
    /// 取句柄所属卷的序列号（不解路径，因而不会像 NtQueryObject 那样阻塞）。
    /// 设备句柄、远端句柄、权限不足都会失败——这正是我们要挡掉的那批。
    /// </summary>
    private static bool TryGetVolumeSerial(IntPtr handle, out ulong serial)
    {
        serial = 0;
        int size = Marshal.SizeOf<FileLockNative.FileIdInfo128>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!FileLockNative.GetFileInformationByHandleEx(
                    handle, FileLockNative.FileIdInfo, buffer, (uint)size))
                return false;

            serial = Marshal.PtrToStructure<FileLockNative.FileIdInfo128>(buffer).VolumeSerialNumber;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------- 匹配（internal static，便于单测） ----------

    /// <summary>
    /// 判断候选句柄名是否指向目标。比较的是内核设备名（\Device\HarddiskVolumeN\...），
    /// 因此不受 DriveLetter / subst 影响。
    /// 文件：完全相等（不区分大小写）；
    /// 目录：相等或目标前缀 + '\\'——必须带分隔符，否则 "\...\dir" 会误配 "\...\directory"。
    /// </summary>
    internal static bool MatchesTargetPath(string targetKernelName, string candidateName, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetKernelName) || string.IsNullOrWhiteSpace(candidateName))
            return false;

        string target = targetKernelName.TrimEnd('\\');
        string candidate = candidateName.TrimEnd('\\');

        if (candidate.Equals(target, StringComparison.OrdinalIgnoreCase))
            return true;

        return isDirectory
            && candidate.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 结果条目（internal static，便于单测） ----------

    /// <summary>
    /// 生成交给外部的条目。路径列表必须在这里拷成快照——被放弃的分片线程仍可能 AddHit，
    /// 而 UI 在扫描返回后会直接枚举 LockedPaths。
    /// </summary>
    internal static FileLockEntry SnapshotEntry(int pid, string name, string path, IReadOnlyList<string> paths, DateTime? startUtc)
        => new()
        {
            ProcessId = pid,
            ProcessName = name,
            ProcessPath = path,
            StartTimeUtc = startUtc,
            LockedPaths = paths.ToArray()
        };

    // ---------- 结束进程前的身份核对（internal static，便于单测） ----------

    /// <summary>
    /// 核对 PID 现在指向的进程是否还是扫描时的那一个。确认对话框期间目标可能已退出、
    /// PID 可能被系统复用，直接按 PID 结束会误杀无关进程。
    ///
    /// 判定规则：双方都有值才比较（进程名忽略大小写、映像路径忽略大小写、启动时间容差 1 秒）；
    /// 至少完成一项比较且全部通过才算 Match。名字是 "PID:1234" 兜底值时跳过该项——
    /// 它是「当时读不到名字」的标记，不是真实进程名。
    /// </summary>
    internal static ProcessIdentityCheck VerifyProcessIdentity(
        int pid, string expectedName, string expectedPath, DateTime? expectedStartUtc)
    {
        if (pid <= 4)
            return ProcessIdentityCheck.Mismatch;   // System / Idle 不是工具的目标

        string actualName;
        DateTime? actualStartUtc;
        try
        {
            using var process = Process.GetProcessById(pid);
            actualName = process.ProcessName;
            try { actualStartUtc = process.StartTime.ToUniversalTime(); }
            catch { actualStartUtc = null; }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ProcessIdentityCheck.Exited;     // 进程已经没了
        }
        catch
        {
            return ProcessIdentityCheck.Mismatch;   // 打不开（权限 / 受保护）：信息不足，保守拒绝
        }

        int compared = 0;

        if (!string.IsNullOrEmpty(expectedName)
            && !expectedName.StartsWith(ProcessNameFallbackPrefix, StringComparison.Ordinal))
        {
            compared++;
            if (!string.Equals(expectedName, actualName, StringComparison.OrdinalIgnoreCase))
                return ProcessIdentityCheck.Mismatch;
        }

        string actualPath = QueryProcessPath(pid);
        if (!string.IsNullOrEmpty(expectedPath) && !string.IsNullOrEmpty(actualPath))
        {
            compared++;
            if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
                return ProcessIdentityCheck.Mismatch;
        }

        if (expectedStartUtc is { } expected && actualStartUtc is { } actual)
        {
            compared++;
            if (Math.Abs((expected - actual).TotalSeconds) > 1)
                return ProcessIdentityCheck.Mismatch;
        }

        // 一项都没比成 = 扫到的信息不足以确认身份，宁可拒绝也不能闭眼杀
        return compared > 0 ? ProcessIdentityCheck.Match : ProcessIdentityCheck.Mismatch;
    }

    // ---------- 续跑决策（internal static，便于单测） ----------

    /// <summary>
    /// 决定一个卡住的分片是否、从哪里续跑。
    /// 已放弃的分片不再续跑：它的剩余区间早就交给了新分片，再续一次会让同一区间被反复重扫，
    /// 并把 GuardedHandles 重复计数（摘要里「因卡死跳过 N 个句柄」会虚高）。
    /// </summary>
    internal static bool TryPlanResume(int start, int end, int index, bool abandoned, out int resumeFrom)
    {
        resumeFrom = 0;
        if (abandoned) return false;
        resumeFrom = Math.Max(index + 1, start);
        return resumeFrom < end;
    }

    // ---------- 句柄表 ----------

    /// <summary>
    /// 取一份句柄表快照。返回非托管缓冲，调用方负责 FreeHGlobal。
    /// 头 16 字节是 { NumberOfHandles, Reserved }，其后是变长数组。
    /// </summary>
    private static IntPtr QueryHandleTable(out int handleCount)
    {
        int size = FileLockNative.InitialHandleBufferSize;

        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            int status = FileLockNative.NtQuerySystemInformation(
                FileLockNative.SystemExtendedHandleInformation, buffer, size, out int needed);

            if (status == 0)
            {
                var header = Marshal.PtrToStructure<FileLockNative.SystemHandleInformationExHeader>(buffer);
                handleCount = unchecked((int)header.NumberOfHandles.ToUInt64());
                return buffer;
            }

            Marshal.FreeHGlobal(buffer);

            if (status != FileLockNative.StatusInfoLengthMismatch)
                throw new ScanFailureException(FileLockScanFailure.NtQuerySystemInformationFailed, status,
                    $"NtQuerySystemInformation 失败：0x{status:X8}");

            if (needed <= size) needed = size * 2;
            if (needed > MaxSnapshotBufferBytes)
                throw new ScanFailureException(FileLockScanFailure.HandleTableTooLarge, needed, "句柄表超出上限，放弃扫描");

            size = needed;
        }
    }

    private static IntPtr EntryPointer(IntPtr buffer, int index, int entrySize)
        => buffer + Marshal.SizeOf<FileLockNative.SystemHandleInformationExHeader>() + index * entrySize;

    /// <summary>
    /// 在自己的句柄表条目里找到刚打开的目标句柄，取其 ObjectTypeIndex —— 即本启动周期
    /// File 对象的类型索引。后面靠纯整数比较就能砍掉 90%+ 的无关句柄。
    /// </summary>
    private static ushort ResolveFileTypeIndex(IntPtr ownHandle)
    {
        IntPtr buffer = QueryHandleTable(out int count);
        try
        {
            int entrySize = Marshal.SizeOf<FileLockNative.SystemHandleTableEntryInfoEx>();
            ulong ownPid = (ulong)Environment.ProcessId;
            ulong handleValue = unchecked((ulong)ownHandle.ToInt64());

            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<FileLockNative.SystemHandleTableEntryInfoEx>(EntryPointer(buffer, i, entrySize));
                if (entry.UniqueProcessId.ToUInt64() == ownPid && entry.HandleValue.ToUInt64() == handleValue)
                    return entry.ObjectTypeIndex;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return 0;
    }

    private static string? QueryObjectName(IntPtr handle)
    {
        int size = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            int status = FileLockNative.NtQueryObject(handle, FileLockNative.ObjectNameInformation, buffer, size, out int needed);
            if (status == FileLockNative.StatusInfoLengthMismatch && needed > 0)
            {
                Marshal.FreeHGlobal(buffer);
                size = needed;
                buffer = Marshal.AllocHGlobal(size);
                status = FileLockNative.NtQueryObject(handle, FileLockNative.ObjectNameInformation, buffer, size, out _);
            }

            if (status < 0) return null;

            var unicode = Marshal.PtrToStructure<FileLockNative.UnicodeString>(buffer);
            if (unicode.Buffer == IntPtr.Zero || unicode.Length == 0) return null;

            return Marshal.PtrToStringUni(unicode.Buffer, unicode.Length / 2);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileLock] NtQueryObject 失败: {ex.Message}");
            return null;
        }
        finally
        {
            // 长度不足时会换成更大的缓冲，这里只释放当前这一个。
            try { Marshal.FreeHGlobal(buffer); } catch { /* 已释放 */ }
        }
    }

    // ---------- 显示路径映射 ----------

    /// <summary>设备名（\Device\HarddiskVolume3）→ 盘符（C:）。仅用于显示，匹配仍用内核名。</summary>
    private static Dictionary<string, string> BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                string letter = drive.Name.TrimEnd('\\');
                var target = new StringBuilder(1024);
                if (FileLockNative.QueryDosDeviceW(letter, target, target.Capacity) == 0) continue;

                // QueryDosDevice 对盘符一般返回单个设备名；多字符串以 '\0' 分隔，取第一个。
                string device = target.ToString();
                int end = device.IndexOf('\0');
                if (end >= 0) device = device[..end];
                if (device.Length > 0) map[device] = letter;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileLock] 盘符映射失败（显示将退回内核名）: {ex.Message}");
        }
        return map;
    }

    private static string ToDisplayPath(string kernelName, IReadOnlyDictionary<string, string> deviceMap)
    {
        string bestDevice = "";
        string bestDrive = "";
        foreach (var (device, drive) in deviceMap)
        {
            if (!kernelName.StartsWith(device, StringComparison.OrdinalIgnoreCase)) continue;
            if (device.Length <= bestDevice.Length) continue;

            // 防止 \Device\HarddiskVolume1 误配 \Device\HarddiskVolume11
            if (kernelName.Length > device.Length && kernelName[device.Length] != '\\') continue;

            bestDevice = device;
            bestDrive = drive;
        }

        return bestDevice.Length == 0
            ? kernelName
            : bestDrive + kernelName[bestDevice.Length..];
    }

    // ---------- 进程信息 ----------

    internal static string QueryProcessPath(int pid)
    {
        IntPtr process = FileLockNative.OpenProcess((int)FileLockNative.ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return "";

        try
        {
            var builder = new StringBuilder(1024);
            int length = builder.Capacity;
            return FileLockNative.QueryFullProcessImageNameW(process, 0, builder, ref length)
                ? builder.ToString(0, length)
                : "";
        }
        catch
        {
            return "";
        }
        finally
        {
            FileLockNative.CloseHandle(process);
        }
    }

    /// <summary>进程名 + 映像路径 + 启动时间，按 PID 缓存（与 PortViewerService 的惯例一致）。</summary>
    private static (string Name, string Path, DateTime? StartUtc) GetProcessInfo(
        int pid, Dictionary<int, (string Name, string Path, DateTime? StartUtc)> cache)
    {
        if (cache.TryGetValue(pid, out var cached)) return cached;

        (string Name, string Path, DateTime? StartUtc) info;
        if (pid <= 4)
        {
            info = ("System", "", null);
        }
        else
        {
            string name;
            DateTime? startUtc = null;
            try
            {
                using var process = Process.GetProcessById(pid);
                name = process.ProcessName;
                try { startUtc = process.StartTime.ToUniversalTime(); }
                catch { /* 受保护进程读不到启动时间：身份核对退到名字 / 路径 */ }
            }
            catch
            {
                name = ProcessNameFallbackPrefix + pid;
            }
            info = (name, QueryProcessPath(pid), startUtc);
        }

        cache[pid] = info;
        return info;
    }

    // ---------- 枚举 ----------

    /// <summary>
    /// 扫描过程的可变状态。多个分片线程并行写入（结果集合用 <see cref="_resultLock"/> 保护），
    /// 看门狗线程读进度与分片状态；计数一律走 Interlocked/Volatile 保证可见性。
    /// </summary>
    private sealed class ScanState
    {
        private readonly string _targetKernelName;
        private readonly bool _isDirectory;
        private readonly ushort _fileTypeIndex;
        private readonly ulong _targetVolumeSerial;
        private readonly IReadOnlyDictionary<string, string> _deviceMap;

        private readonly Dictionary<int, (string Name, string Path, DateTime? StartUtc)> _processCache = new();
        private readonly HashSet<int> _skippedPids = new();
        private readonly Dictionary<int, EntryBuilder> _builders = new();

        /// <summary>
        /// 保护结果集合与错误字段。看门狗放弃等待后工作线程可能仍在跑，
        /// 主线程此时要读结果，所以写入与读取都必须串行化。
        /// </summary>
        private readonly object _resultLock = new();

        private int _processed;
        private int _pendingRunners;
        private Exception? _error;

        /// <summary>已启动的句柄区间分片；卡住时会从卡住位置之后续跑一段新的。</summary>
        private readonly List<Runner> _runners = new();
        private int _runnerSeq;

        public ScanState(string targetKernelName, bool isDirectory, ushort fileTypeIndex,
            ulong targetVolumeSerial, IReadOnlyDictionary<string, string> deviceMap)
        {
            _targetKernelName = targetKernelName;
            _isDirectory = isDirectory;
            _fileTypeIndex = fileTypeIndex;
            _targetVolumeSerial = targetVolumeSerial;
            _deviceMap = deviceMap;
        }

        public int Processed => Volatile.Read(ref _processed);

        /// <summary>
        /// 仍在跑的分片数：0 表示全部收尾。看门狗循环与卡死判定用它；
        /// **快照能否释放改看 _snapshotOwners**（看门狗自己也算一个持有者）。
        /// </summary>
        public int PendingRunners => Volatile.Read(ref _pendingRunners);

        public Exception? Error
        {
            get { lock (_resultLock) return _error; }
        }

        public bool Truncated { get; set; }

        private int _scannedHandles;
        private int _guardedHandles;

        public int ScannedHandles => Volatile.Read(ref _scannedHandles);
        public int GuardedHandles => Volatile.Read(ref _guardedHandles);

        public int SkippedProcesses
        {
            get { lock (_resultLock) return _skippedPids.Count; }
        }

        private void MarkSkipped(int pid)
        {
            lock (_resultLock) _skippedPids.Add(pid);
        }

        // ---------- 句柄表快照的所有权 ----------

        private IntPtr _snapshotBuffer;

        /// <summary>
        /// 快照持有者名额：主线程（看门狗随时可能再拉起续跑分片）算一个，每个分片线程算一个。
        /// **看门狗必须计入**：它准备续跑时会 Launch 新分片，若那时分片数恰好归零就释放了快照，
        /// 新分片会读到已释放的内存（use-after-free）。
        /// </summary>
        private int _snapshotOwners;

        /// <summary>快照是否已接管（0/1）。没接管过就没有账可还，避免误减成负数。</summary>
        private int _snapshotAttached;

        /// <summary>接管句柄表快照：主线程自己先占一个持有名额。</summary>
        public void AttachSnapshotBuffer(IntPtr buffer)
        {
            _snapshotBuffer = buffer;
            _snapshotOwners = 1;
            _snapshotAttached = 1;
            Interlocked.Increment(ref _liveSnapshotBuffers);
        }

        /// <summary>分片启动前占名额（在 Thread.Start 之前调用，保证不晚于任何释放）。</summary>
        private void AcquireSnapshotOwner() => Interlocked.Increment(ref _snapshotOwners);

        /// <summary>
        /// 一个持有者（主线程收尾 / 分片线程 finally）到期。名额归零才真正释放；
        /// Interlocked.Exchange 取走指针保证**恰好释放一次**（两次就是堆损坏）。
        /// 真正永久卡死的分片会让名额永远不归零——那一块快照只能留在原地，
        /// 与被放弃的线程同一取舍（见 RunWithWatchdog 的说明）。
        /// </summary>
        public void ReleaseSnapshotOwner()
        {
            if (Volatile.Read(ref _snapshotAttached) == 0) return;
            if (Interlocked.Decrement(ref _snapshotOwners) != 0) return;

            IntPtr buffer = Interlocked.Exchange(ref _snapshotBuffer, IntPtr.Zero);
            if (buffer == IntPtr.Zero) return;

            Marshal.FreeHGlobal(buffer);
            Interlocked.Decrement(ref _liveSnapshotBuffers);
        }

        /// <summary>把句柄表切成 workerCount 片，各自起一个线程枚举。</summary>
        public void StartInitialSlices(IntPtr buffer, int count, int workerCount,
            CancellationToken cancellationToken, IProgress<string>? progress)
        {
            int slice = (count + workerCount - 1) / workerCount;

            for (int w = 0; w < workerCount; w++)
            {
                int start = w * slice;
                int end = Math.Min(start + slice, count);
                if (start < end) Launch(buffer, count, start, end, cancellationToken, progress);
            }
        }

        /// <summary>
        /// 卡住恢复：每个没跑完的分片，把它卡住的那一个句柄记为「为防卡死而跳过」，
        /// 然后从该句柄之后续跑剩余区间。返回续跑的分片数（0 表示没法再恢复）。
        /// 已被放弃的分片不再进入候选（见 TryPlanResume）。
        /// </summary>
        public int RecoverStalled(IntPtr buffer, int count,
            CancellationToken cancellationToken, IProgress<string>? progress)
        {
            List<Runner> stalled;
            lock (_runners) stalled = _runners.Where(r => !r.Done).ToList();

            int resumed = 0;
            foreach (var runner in stalled)
            {
                // 逻辑放弃与物理完成必须分开：把仍卡着的线程提前标成 Done，看门狗会以为分片都收尾了，
                // 而它名下的快照持有名额永远不回还（快照再也释放不掉）。
                bool alreadyAbandoned = Interlocked.CompareExchange(ref runner.Abandoned, 1, 0) == 1;
                int stuckAt = Volatile.Read(ref runner.Index);
                if (!TryPlanResume(runner.Start, runner.End, stuckAt, alreadyAbandoned, out int resumeFrom))
                    continue;

                // 记入 GuardedHandles：这个句柄因为会卡死被跳过了，用户有权知道可能漏报。
                Interlocked.Increment(ref _guardedHandles);
                Launch(buffer, count, resumeFrom, runner.End, cancellationToken, progress);
                resumed++;
            }

            return resumed;
        }

        private void Launch(IntPtr buffer, int count, int start, int end,
            CancellationToken cancellationToken, IProgress<string>? progress)
        {
            var runner = new Runner { Start = start, End = end, Index = start - 1 };
            lock (_runners) _runners.Add(runner);

            Interlocked.Increment(ref _pendingRunners);
            Interlocked.Increment(ref _activeScanRunners);

            // 先占快照名额再启动线程：释放以「名额归零」为准，新分片因此不可能在别人已释放后起跑。
            // 名额的获取与线程构造都放进 try：中途抛异常时名额必须一起还回去，否则快照再也释放不掉。
            try
            {
                AcquireSnapshotOwner();

                var thread = new Thread(() => RunRunner(runner, buffer, count, cancellationToken, progress))
                {
                    IsBackground = true,
                    Name = $"FileLockScan{Interlocked.Increment(ref _runnerSeq)}"
                };

                thread.Start();
            }
            catch
            {
                // 线程没起来（含构造失败）：把名额与计数还回去，否则快照再也没人释放
                runner.Done = true;
                Interlocked.Decrement(ref _pendingRunners);
                Interlocked.Decrement(ref _activeScanRunners);
                ReleaseSnapshotOwner();
                throw;
            }
        }

        private void RunRunner(Runner runner, IntPtr buffer, int count,
            CancellationToken cancellationToken, IProgress<string>? progress)
        {
            try
            {
                EnumerateRange(runner, buffer, count, cancellationToken, progress);
            }
            catch (Exception ex)
            {
                lock (_resultLock) _error ??= ex;
            }
            finally
            {
                // 先置 Done 再减计数：PendingRunners 归零时保证没有任何线程还在读缓冲。
                runner.Done = true;
                Interlocked.Decrement(ref _pendingRunners);

                // 先交还快照名额、再减「活动分片数」：测试用 ActiveScanRunners 归零后立刻断言
                // 快照已释放，顺序反了会读到「分片已清空、但还没释放」的中间态。
                ReleaseSnapshotOwner();
                Interlocked.Decrement(ref _activeScanRunners);
            }
        }

        /// <summary>一个句柄区间分片。[Index] 是它当前处理到的位置，卡住时据此定位。</summary>
        private sealed class Runner
        {
            public int Start;
            public int End;
            public int Index;
            public volatile bool Done;

            /// <summary>
            /// 1 = 剩余区间已交给新分片，不再参与续跑（见 TryPlanResume）。
            /// 与 [Done] 必须分开：这里只表示「逻辑上放弃」，物理线程可能仍卡在原生调用里。
            /// 提前把卡住的线程标成 Done 会让看门狗以为分片都收尾了，而那个线程名下的
            /// 快照持有名额永远不回还——整块快照再也释放不掉。
            /// 仅用 Interlocked 访问。
            /// </summary>
            public int Abandoned;
        }

        private void EnumerateRange(Runner runner, IntPtr buffer, int totalCount,
            CancellationToken cancellationToken, IProgress<string>? progress)
        {
            int entrySize = Marshal.SizeOf<FileLockNative.SystemHandleTableEntryInfoEx>();
            IntPtr currentProcess = FileLockNative.GetCurrentProcess();

            int lastReported = 0;

            for (int i = runner.Start; i < runner.End; i++)
            {
                TestPerHandleHook?.Invoke(i);
                Volatile.Write(ref runner.Index, i);
                Interlocked.Increment(ref _scannedHandles);
                Interlocked.Increment(ref _processed);

                if ((i & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i - lastReported >= 0x3FF)
                    {
                        lastReported = i;
                        progress?.Report(string.Format(
                            LocalizationService.L("FileLock_Progress", "已检查 {0} / {1} 个句柄"),
                            Processed, totalCount));
                    }
                }

                    var entry = Marshal.PtrToStructure<FileLockNative.SystemHandleTableEntryInfoEx>(
                        EntryPointer(buffer, i, entrySize));

                    // 第 2 层：只留 File 类型（纯整数比较，砍掉 90%+）
                    if (entry.ObjectTypeIndex != _fileTypeIndex) continue;

                    int pid = unchecked((int)entry.UniqueProcessId.ToUInt64());
                    if (pid <= 4)
                    {
                        MarkSkipped(pid);
                        continue;
                    }

                    IntPtr sourceProcess = FileLockNative.OpenProcess((int)FileLockNative.ProcessDupHandle, false, pid);
                    if (sourceProcess == IntPtr.Zero)
                    {
                        MarkSkipped(pid);   // 受保护/已退出进程打不开：如实计数
                        continue;
                    }

                    try
                    {
                        IntPtr handleValue = new(unchecked((long)entry.HandleValue.ToUInt64()));
                        if (!FileLockNative.DuplicateHandle(sourceProcess, handleValue, currentProcess,
                                out IntPtr local, 0, false, FileLockNative.DuplicateSameAccess))
                        {
                            MarkSkipped(pid);
                            continue;
                        }

                        try
                        {
                            // 第 3 层：只处理磁盘文件/目录（管道、事件、互斥体等在这里被剔除）。
                            // 这一层同时就是**防卡死的屏障**：NtQueryObject 只在管道/设备这类
                            // 同步对象上才会永久阻塞，而它们全都过不了 GetFileType == FILE_TYPE_DISK。
                            if (FileLockNative.GetFileType(local) != FileLockNative.FileTypeDisk) continue;

                            // 第 4 层：同卷预筛。卷序列号取不到（设备/远端/权限不足）或与目标不同卷的，
                            // 一律跳过——它们不可能命中目标，而下一层的 NtQueryObject 恰恰是唯一
                            // 可能长时间阻塞的一步（实测某显卡驱动的一个句柄能卡住 10 秒以上）。
                            if (_targetVolumeSerial != 0)
                            {
                                if (!TryGetVolumeSerial(local, out ulong volumeSerial)) continue;
                                if (volumeSerial != _targetVolumeSerial) continue;
                            }

                            // 第 5 层：只有走到这里的存活句柄才敢查名字
                            string? name = QueryObjectName(local);
                            if (name is null) continue;
                            if (!MatchesTargetPath(_targetKernelName, name, _isDirectory)) continue;

                            AddHit(pid, name);
                        }
                        finally
                        {
                            FileLockNative.CloseHandle(local);
                        }
                    }
                    finally
                    {
                        FileLockNative.CloseHandle(sourceProcess);
                    }
                }
        }

        private void AddHit(int pid, string kernelName)
        {
            lock (_resultLock)
            {
                if (!_builders.TryGetValue(pid, out var builder))
                {
                    var (name, path, startUtc) = GetProcessInfo(pid, _processCache);
                    builder = new EntryBuilder(pid, name, path, startUtc);
                    _builders[pid] = builder;
                }
                builder.Add(ToDisplayPath(kernelName, _deviceMap));
            }
        }

        public IReadOnlyList<FileLockEntry> BuildEntries()
        {
            lock (_resultLock)
            {
                return _builders.Values
                    .OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(b => b.ProcessId)
                    .Select(b => SnapshotEntry(b.ProcessId, b.Name, b.Path, b.Paths, b.StartTimeUtc))
                    .ToList();
            }
        }

        private sealed class EntryBuilder(int pid, string name, string path, DateTime? startUtc)
        {
            private readonly List<string> _paths = [];
            private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

            public int ProcessId { get; } = pid;
            public string Name { get; } = name;
            public string Path { get; } = path;
            public DateTime? StartTimeUtc { get; } = startUtc;
            public IReadOnlyList<string> Paths => _paths;

            public void Add(string displayPath)
            {
                if (_seen.Add(displayPath)) _paths.Add(displayPath);
            }
        }
    }
}
