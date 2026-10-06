package com.tubawinui3.remote

import android.app.Application
import android.graphics.BitmapFactory
import android.os.Build
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.tubawinui3.remote.data.ApiClient
import com.tubawinui3.remote.data.ApiException
import com.tubawinui3.remote.data.ChatMessage
import com.tubawinui3.remote.data.ConnectTarget
import com.tubawinui3.remote.data.HardwareSection
import com.tubawinui3.remote.data.PcStore
import com.tubawinui3.remote.data.SavedPc
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** 电脑在线状态（「我的电脑」列表展示）。 */
enum class PcStatus { Unknown, Checking, Online, Offline }

class RemoteViewModel(app: Application) : AndroidViewModel(app) {
    private val prefs = app.getSharedPreferences("remote", 0)
    private val pcsPrefKey = "pcs"
    private val lastPcPrefKey = "lastPcId"

    var client by mutableStateOf<ApiClient?>(null)
        private set
    var hostName by mutableStateOf("")
        private set
    var connecting by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    var hardware by mutableStateOf<List<HardwareSection>?>(null)
        private set
    var hardwareError by mutableStateOf<String?>(null)
        private set
    var screenshot by mutableStateOf<ImageBitmap?>(null)
        private set
    var screenshotLoading by mutableStateOf(false)
        private set
    var screenshotError by mutableStateOf<String?>(null)
        private set
    val messages = mutableStateListOf<ChatMessage>()

    /** 已记住的电脑（最近连接优先）。 */
    val devices = mutableStateListOf<SavedPc>()

    /** 每台电脑的在线状态。 */
    val statuses = mutableStateMapOf<String, PcStatus>()

    /** 需要用户输入配对码的目标（未配对 / 配对失效），由列表页弹出配对面板消费。 */
    var pairTarget by mutableStateOf<ConnectTarget?>(null)
        private set

    val connected get() = client != null

    private var pollJob: Job? = null
    private var clientPcId: String? = null

    init {
        var list = PcStore.decode(prefs.getString(pcsPrefKey, null))
        if (list.isEmpty()) {
            // 旧版单机数据迁移：host/port/token → 单条目
            val legacy = PcStore.migrate(
                prefs.getString("host", null),
                prefs.getInt("port", 18765),
                prefs.getString("token", null),
            )
            if (legacy.isNotEmpty()) {
                list = legacy
                prefs.edit()
                    .putString(pcsPrefKey, PcStore.encode(list))
                    .putString(lastPcPrefKey, legacy.first().id)
                    .remove("host")
                    .remove("port")
                    .remove("token")
                    .apply()
            }
        }
        devices.addAll(list)
    }

    // ───────── 设备列表 ─────────

    private fun saveDevices(list: List<SavedPc>) {
        devices.clear()
        devices.addAll(list)
        prefs.edit().putString(pcsPrefKey, PcStore.encode(list)).apply()
    }

    fun removeDevice(id: String) {
        if (clientPcId == id) disconnect()
        saveDevices(PcStore.remove(devices, id))
        statuses.remove(id)
        if (prefs.getString(lastPcPrefKey, null) == id) prefs.edit().remove(lastPcPrefKey).apply()
    }

    /** 并发探测各电脑在线状态（ping 不需要令牌）；在线时顺手把电脑名刷新回来。 */
    fun refreshStatuses() {
        val list = devices.toList()
        for (pc in list) {
            if (statuses[pc.id] == PcStatus.Checking) continue
            if (statuses[pc.id] == null) statuses[pc.id] = PcStatus.Checking
            viewModelScope.launch {
                val name = runCatching { ApiClient(pc.host, pc.port).ping() }.getOrNull()
                statuses[pc.id] = if (name != null) PcStatus.Online else PcStatus.Offline
                if (!name.isNullOrEmpty() && name != pc.name) {
                    saveDevices(PcStore.upsert(devices, pc.copy(name = name)))
                }
            }
        }
    }

    // ───────── 连接 ─────────

    /** 启动时用上次电脑的令牌自动重连；失败（或无令牌）回到「我的电脑」列表。 */
    fun tryRestore() {
        val lastId = prefs.getString(lastPcPrefKey, null) ?: return
        val pc = devices.firstOrNull { it.id == lastId } ?: return
        val token = pc.token ?: return
        connecting = true
        viewModelScope.launch {
            val c = ApiClient(pc.host, pc.port, token)
            try {
                c.info()
                hostName = c.ping().ifEmpty { pc.name.ifEmpty { pc.host } }
                onConnected(c, pc)
            } catch (e: ApiException) {
                if (e.code == 401) updateToken(pc.id, null)
            } finally {
                connecting = false
            }
        }
    }

    /** 列表点按：有令牌直接连（401 转配对），没有令牌则请用户输入配对码。 */
    fun requestConnect(pc: SavedPc) {
        if (connecting) return
        val token = pc.token
        if (token == null) {
            pairTarget = ConnectTarget(pc.host, pc.port, name = pc.name.ifEmpty { null })
            return
        }
        connecting = true
        error = null
        viewModelScope.launch {
            val c = ApiClient(pc.host, pc.port, token)
            try {
                c.info()
                hostName = c.ping().ifEmpty { pc.name.ifEmpty { pc.host } }
                onConnected(c, pc)
            } catch (e: ApiException) {
                if (e.code == 401) {
                    updateToken(pc.id, null)
                    pairTarget = ConnectTarget(pc.host, pc.port, name = pc.name.ifEmpty { null })
                    error = "配对已失效，请输入电脑上的 6 位配对码"
                } else {
                    error = e.message
                }
            } finally {
                connecting = false
            }
        }
    }

    fun clearPairTarget() {
        pairTarget = null
    }

    /** 用配对码连接（添加新电脑 / 重新配对都走这里）。 */
    fun connect(target: ConnectTarget, code: String) {
        if (connecting) return
        if (code.isBlank()) { error = "请输入电脑上显示的 6 位配对码"; return }
        connecting = true
        error = null
        viewModelScope.launch {
            val c = ApiClient(target.host, target.port)
            try {
                val name = c.pair(code.trim(), "${Build.MANUFACTURER} ${Build.MODEL}")
                val pc = SavedPc(
                    host = target.host,
                    port = target.port,
                    name = target.name ?: name,
                    token = c.token,
                    lastConnectedAt = System.currentTimeMillis(),
                )
                saveDevices(PcStore.upsert(devices, pc))
                hostName = pc.name
                onConnected(c, pc)
                pairTarget = null
            } catch (e: ApiException) {
                error = e.message
            } finally {
                connecting = false
            }
        }
    }

    fun scanError(msg: String) {
        error = msg
    }

    /** 断开当前连接（回到列表）：保留条目与令牌，再次连接无需重新配对。 */
    fun disconnect() {
        pollJob?.cancel()
        client = null
        clientPcId = null
        hardware = null
        screenshot = null
        messages.clear()
    }

    private fun updateToken(id: String, token: String?) {
        saveDevices(devices.map { if (it.id == id) it.copy(token = token) else it })
    }

    private fun onConnected(c: ApiClient, pc: SavedPc) {
        client = c
        clientPcId = pc.id
        error = null
        messages.clear()
        prefs.edit().putString(lastPcPrefKey, pc.id).apply()
        saveDevices(PcStore.touch(devices, pc.id, System.currentTimeMillis()))
        refreshScreenshot()
        loadHardware()
        pollJob?.cancel()
        pollJob = viewModelScope.launch {
            var failures = 0
            while (isActive) {
                try {
                    pollChat(c)
                    failures = 0
                } catch (e: ApiException) {
                    if (e.code == 401 || ++failures >= 4) {
                        error = if (e.code == 401) "连接已失效，请重新配对" else "与电脑的连接已中断"
                        disconnectKeepTarget()
                        return@launch
                    }
                }
                delay(2000)
            }
        }
    }

    private fun disconnectKeepTarget() {
        client = null
        clientPcId = null
        hardware = null
        screenshot = null
    }

    private suspend fun pollChat(c: ApiClient) {
        val last = messages.lastOrNull()?.seq ?: 0
        val fresh = c.chatList(last)
        if (fresh.isNotEmpty()) messages.addAll(fresh.filter { m -> messages.none { it.seq == m.seq } })
    }

    fun addMessage(m: ChatMessage) {
        if (messages.none { it.seq == m.seq }) messages.add(m)
    }

    fun loadHardware() {
        val c = client ?: return
        viewModelScope.launch {
            try {
                hardware = c.hardware()
                hardwareError = null
            } catch (e: ApiException) {
                hardwareError = e.message
            }
        }
    }

    fun refreshScreenshot() {
        val c = client ?: return
        if (screenshotLoading) return
        screenshotLoading = true
        viewModelScope.launch {
            try {
                val bytes = c.screenshot()
                screenshot = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
                screenshotError = null
            } catch (e: ApiException) {
                screenshotError = e.message
            } finally {
                screenshotLoading = false
            }
        }
    }
}
