package com.tubawinui3.remote

import android.app.Application
import android.graphics.BitmapFactory
import android.os.Build
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
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
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

class RemoteViewModel(app: Application) : AndroidViewModel(app) {
    private val prefs = app.getSharedPreferences("remote", 0)

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

    val connected get() = client != null

    private var pollJob: Job? = null

    /** 启动时尝试用上次保存的令牌自动重连；失败则回到连接界面。 */
    fun tryRestore() {
        val host = prefs.getString("host", null) ?: return
        val token = prefs.getString("token", null) ?: return
        val c = ApiClient(host, prefs.getInt("port", 18765), token)
        connecting = true
        viewModelScope.launch {
            try {
                c.info()
                hostName = c.ping().ifEmpty { host }
                onConnected(c)
            } catch (e: ApiException) {
                if (e.code == 401) prefs.edit().remove("token").apply()
            } finally {
                connecting = false
            }
        }
    }

    val lastTarget: ConnectTarget?
        get() = prefs.getString("host", null)?.let { ConnectTarget(it, prefs.getInt("port", 18765)) }

    fun connect(target: ConnectTarget, code: String) {
        if (connecting) return
        if (code.isBlank()) { error = "请输入电脑上显示的 6 位配对码"; return }
        connecting = true
        error = null
        val c = ApiClient(target.host, target.port)
        viewModelScope.launch {
            try {
                val name = c.pair(code.trim(), "${Build.MANUFACTURER} ${Build.MODEL}")
                hostName = target.name ?: name
                prefs.edit().putString("host", target.host).putInt("port", target.port).putString("token", c.token).apply()
                onConnected(c)
            } catch (e: ApiException) {
                error = e.message
            } finally {
                connecting = false
            }
        }
    }

    fun scanError(msg: String) { error = msg }

    fun disconnect() {
        pollJob?.cancel()
        client = null
        hardware = null
        screenshot = null
        messages.clear()
        prefs.edit().remove("token").apply()
    }

    private fun onConnected(c: ApiClient) {
        client = c
        error = null
        messages.clear()
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
