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
            return new FileLockScanResult
            {
                Error = FileLockScanError.ResolveFailed,
                ErrorDetail = $"CreateFileW 失败（错误码 {Marshal.GetLastWin32Error()}）"
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
                    ErrorDetail = "目标不是磁盘上的文件或目录"
                };
            }

            targetKernelName = QueryObjectName(ownHandle) ?? "";
            if (targetKernelName.Length == 0)
            {
                return new FileLockScanResult
                {
                    Error = FileLockScanError.ResolveFailed,
                    ErrorDetail = "无法解析目标的内核设备名"
                };
            }

            fileTypeIndex = ResolveFileTypeIndex(ownHandle);
            if (fileTypeIndex == 0)
            {
                return new FileLockScanResult
                {
                    Error = FileLockScanError.ResolveFailed,
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

        RunWithWatchdog(state, cancellationToken, progress);

        // 取消与看门狗放弃都可能发生在工作线程还在跑的时候，这里统一按「已取消」上抛。
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        var error = FileLockScanError.None;
        string? detail = null;
        if (state.Error is OperationCanceledException canceled)
            throw canceled;
        if (state.Error is not null)
        {
            error = FileLockScanError.Failed;
            detail = state.Error.Message;
        }

        return new FileLockScanResult
        {
            Entries = state.BuildEntries(),
            ScannedHandles = state.ScannedHandles,
            SkippedProcesses = state.SkippedProcesses,
            GuardedHandles = state.GuardedHandles,
            Truncated = state.Truncated,
            Error = error,
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

        if (state.PendingRunners == 0)
        {
            // 全部收尾：此时不会再有任何线程读缓冲，可以安全释放。
            Marshal.FreeHGlobal(buffer);
        }
        else
        {
            // 放弃等待：绝不在这里释放 buffer —— 被放弃的线程可能仍持有该指针，
            // 释放会变成 use-after-free 崩溃。宁可泄漏这一块内存（与泄漏线程同一取舍）。
            state.Truncated = true;
        }
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
                throw new InvalidOperationException($"NtQuerySystemInformation 失败：0x{status:X8}");

            if (needed <= size) needed = size * 2;
            if (needed > FileLockNative.MaxHandleBufferSize)
                throw new InvalidOperationException("句柄表超出上限，放弃扫描");

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

    private static string QueryProcessPath(int pid)
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

    /// <summary>进程名 + 映像路径，按 PID 缓存（与 PortViewerService 的惯例一致）。</summary>
    private static (string Name, string Path) GetProcessInfo(int pid, Dictionary<int, (string Name, string Path)> cache)
    {
        if (cache.TryGetValue(pid, out var cached)) return cached;

        (string Name, string Path) info;
        if (pid <= 4)
        {
            info = ("System", "");
        }
        else
        {
            string name;
            try
            {
                using var process = Process.GetProcessById(pid);
                name = process.ProcessName;
            }
            catch
            {
                name = $"PID:{pid}";
            }
            info = (name, QueryProcessPath(pid));
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

        private readonly Dictionary<int, (string Name, string Path)> _processCache = new();
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

        /// <summary>仍在跑的分片数；0 表示全部收尾（此时可以安全释放句柄表缓冲）。</summary>
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
        /// </summary>
        public int RecoverStalled(IntPtr buffer, int count,
            CancellationToken cancellationToken, IProgress<string>? progress)
        {
            List<Runner> stalled;
            lock (_runners) stalled = _runners.Where(r => !r.Done).ToList();

            int resumed = 0;
            foreach (var runner in stalled)
            {
                int stuckAt = Volatile.Read(ref runner.Index);
                int resumeFrom = Math.Max(stuckAt + 1, runner.Start);
                if (resumeFrom >= runner.End) continue;

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
            var thread = new Thread(() => RunRunner(runner, buffer, count, cancellationToken, progress))
            {
                IsBackground = true,
                Name = $"FileLockScan{Interlocked.Increment(ref _runnerSeq)}"
            };
            thread.Start();
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
            }
        }

        /// <summary>一个句柄区间分片。[Index] 是它当前处理到的位置，卡住时据此定位。</summary>
        private sealed class Runner
        {
            public int Start;
            public int End;
            public int Index;
            public volatile bool Done;
        }

        private void EnumerateRange(Runner runner, IntPtr buffer, int totalCount,
            CancellationToken cancellationToken, IProgress<string>? progress)
        {
            int entrySize = Marshal.SizeOf<FileLockNative.SystemHandleTableEntryInfoEx>();
            IntPtr currentProcess = FileLockNative.GetCurrentProcess();

            int lastReported = 0;

            for (int i = runner.Start; i < runner.End; i++)
            {
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
                    var (name, path) = GetProcessInfo(pid, _processCache);
                    builder = new EntryBuilder(pid, name, path);
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
                    .Select(b => new FileLockEntry
                    {
                        ProcessId = b.ProcessId,
                        ProcessName = b.Name,
                        ProcessPath = b.Path,
                        LockedPaths = b.Paths
                    })
                    .ToList();
            }
        }

        private sealed class EntryBuilder(int pid, string name, string path)
        {
            private readonly List<string> _paths = [];
            private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

            public int ProcessId { get; } = pid;
            public string Name { get; } = name;
            public string Path { get; } = path;
            public IReadOnlyList<string> Paths => _paths;

            public void Add(string displayPath)
            {
                if (_seen.Add(displayPath)) _paths.Add(displayPath);
            }
        }
    }
}
